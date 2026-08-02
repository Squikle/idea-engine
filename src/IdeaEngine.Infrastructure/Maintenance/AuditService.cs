using System.Globalization;
using System.Net;
using System.Text;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Maintenance;

public sealed record AuditResult(string Html, string? StoppedReason, IReadOnlyList<long> UnresearchedIds);

/// <summary>
/// Pipeline integrity sweep: finds ideas that fell through the cracks (dropped but never
/// researched, stuck candidates, failed jobs, unreviewed verdicts), posts the list, and
/// appends a short reflection to journal/advice.md for the architect session to act on.
/// </summary>
public sealed class AuditService(
    IdeaEngineDbContext db,
    OpenRouterChatClient chat,
    BudgetGuard budgetGuard,
    IAdviceJournal adviceJournal,
    IStatusTracker statusTracker,
    TimeProvider timeProvider,
    IOptions<GlanceOptions> nanoOptions,
    ILogger<AuditService> logger)
{
    private const string StageName = "audit";

    private const string ReflectSystem =
        """
        You review pipeline health stats for an idea-discovery system and write ONE short
        paragraph (max 80 words) of concrete observations: patterns in what falls through,
        and 1-2 specific improvement suggestions (niches, sources, process). Plain text only.
        """;

    public async Task<AuditResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await statusTracker.BeginAsync(Tracks.Audit, "sweeping for leaks…", cancellationToken);

        // Ideas from /drop or /dig that never reached research.
        var researchedIds = db.ResearchReports.Select(r => r.IdeaId);
        var neverResearched = await db.Ideas
            .Where(i => i.Category != "meta" && i.Status == "candidate"
                && !researchedIds.Contains(i.Id))
            .OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.Title, i.Origin, i.CreatedAt })
            .ToListAsync(cancellationToken);

        var failedJobs = await db.Jobs
            .Where(j => j.Status == "failed")
            .OrderByDescending(j => j.Id)
            .Take(5)
            .Select(j => new { j.Id, j.Kind, j.LastError })
            .ToListAsync(cancellationToken);

        var unreviewed = await db.Ideas.CountAsync(
            i => i.Category != "meta" && !i.Verified
                && (i.Status == "uncertain" || i.Status == "validated" || i.Status == "hot"),
            cancellationToken);

        var dismissedUnseen = await db.Ideas.CountAsync(
            i => i.Category != "meta" && !i.Verified && i.Status == "dismissed"
                && i.CreatedAt >= now.AddDays(-7),
            cancellationToken);

        var stale = neverResearched.Where(i => i.CreatedAt < now.AddHours(-24)).ToList();

        var builder = new StringBuilder("🧾 <b>Audit</b>\n");
        if (neverResearched.Count == 0 && failedJobs.Count == 0)
        {
            builder.Append("✅ no leaks: every live idea reached research, no failed jobs\n");
        }

        if (neverResearched.Count > 0)
        {
            builder.Append("\n<b>🌱 Never researched (").Append(neverResearched.Count).Append(")</b>");
            builder.Append(" — batch: /research ")
                .Append(string.Join(' ', neverResearched.Take(8).Select(i => i.Id))).Append('\n');
            foreach (var idea in neverResearched.Take(8))
            {
                var age = (now - idea.CreatedAt).TotalHours;
                builder.Append("• #").Append(idea.Id).Append(' ')
                    .Append(WebUtility.HtmlEncode(TextClip.Clip(idea.Title, 55)))
                    .Append(" <i>(").Append(idea.Origin).Append(", ")
                    .Append(age < 48 ? $"{age:F0}h" : $"{age / 24:F0}d").Append(" old)</i>\n");
            }
        }

        if (failedJobs.Count > 0)
        {
            builder.Append("\n<b>⛔ Failed jobs</b>\n");
            foreach (var job in failedJobs)
            {
                builder.Append("• #").Append(job.Id).Append(' ').Append(job.Kind).Append(" — ")
                    .Append(WebUtility.HtmlEncode(TextClip.Clip(job.LastError ?? "?", 70)))
                    .Append('\n');
            }

            builder.Append("<i>/queue has retry buttons</i>\n");
        }

        builder.Append("\n👁 review inbox: ").Append(unreviewed)
            .Append(" researched idea(s) await your eyes (/ideas)\n");
        if (dismissedUnseen > 0)
        {
            builder.Append("☠️ ").Append(dismissedUnseen)
                .Append(" auto-killed this week without your review (/ideas dead)\n");
        }

        // Reflection paragraph (nano, tiny cap) - appended to the advice journal.
        var reflection = await ReflectAsync(
            $"never_researched={neverResearched.Count} (stale>24h={stale.Count}), " +
            $"failed_jobs={failedJobs.Count}, unreviewed={unreviewed}, " +
            $"dismissed_unseen_7d={dismissedUnseen}, " +
            $"origins_stuck=[{string.Join(',', neverResearched.GroupBy(i => i.Origin).Select(g => $"{g.Key}:{g.Count()}"))}]",
            cancellationToken);

        if (reflection is { Length: > 0 })
        {
            builder.Append("\n💭 <i>").Append(WebUtility.HtmlEncode(TextClip.Clip(reflection, 350))).Append("</i>");
            await adviceJournal.AppendAsync(
                $"## {now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC · audit\n{reflection}\n",
                cancellationToken);
        }

        await statusTracker.EndAsync(
            Tracks.Audit,
            neverResearched.Count == 0 && failedJobs.Count == 0
                ? "no leaks"
                : $"{neverResearched.Count} unresearched · {failedJobs.Count} failed",
            CancellationToken.None);
        logger.LogInformation(
            "Audit: {NeverResearched} unresearched, {Failed} failed jobs, {Unreviewed} unreviewed",
            neverResearched.Count, failedJobs.Count, unreviewed);

        return new AuditResult(builder.ToString().TrimEnd(), null, [.. neverResearched.Take(8).Select(i => i.Id)]);
    }

    private async Task<string?> ReflectAsync(string stats, CancellationToken cancellationToken)
    {
        if (!chat.IsConfigured)
        {
            return null;
        }

        var options = nanoOptions.Value;
        var worstCall = (1_000m * options.InputPricePerMTok
            + 600 * options.OutputPricePerMTok) / 1_000_000m;
        var check = await budgetGuard.CheckAsync(
            StageName, 0.05m, worstCall, worstCall, cancellationToken);
        if (!check.Allowed)
        {
            return null;
        }

        var completion = await chat.CompleteAsync(
            options.Model,
            ReflectSystem + "\nReply with ONLY {\"reflection\":\"...\"}",
            stats, 600, "low", cancellationToken);

        if (completion is not null)
        {
            db.AiLedger.Add(new AiLedgerEntry
            {
                Day = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime),
                Stage = StageName,
                Model = options.Model,
                TokensIn = completion.TokensIn,
                TokensOut = completion.TokensOut,
                CostUsd = (completion.TokensIn * options.InputPricePerMTok
                    + completion.TokensOut * options.OutputPricePerMTok) / 1_000_000m,
                CreatedAt = timeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        return LlmJson.TryParse<ReflectionDto>(completion?.Content)?.Reflection;
    }

    private sealed record ReflectionDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("reflection")] string? Reflection);
}
