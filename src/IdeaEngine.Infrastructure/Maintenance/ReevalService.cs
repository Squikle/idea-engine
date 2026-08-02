using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ideation;
using IdeaEngine.Infrastructure.Jobs;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using IdeaEngine.Infrastructure.Research;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Maintenance;

/// <summary>Bound from configuration section <c>IdeaEngine:Ai:Reeval</c>.</summary>
public sealed class ReevalOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Stage-0 shortlist size passed to the nano screener.</summary>
    public int ShortlistSize { get; set; } = 12;

    /// <summary>Heuristic priority floor to enter the shortlist.</summary>
    public double MinPriority { get; set; } = 0.25;

    /// <summary>Screener verdicts at/above this are re-research-worthy.</summary>
    public double WorthThreshold { get; set; } = 0.6;

    /// <summary>How many worthy ideas get auto-queued per sweep (rest = one-tap button).</summary>
    public int AutoQueueTop { get; set; } = 2;

    /// <summary>Nano-only stage cap; actual re-researches bill to the research stage.</summary>
    public decimal DailyUsdCap { get; set; } = 0.10m;
}

public sealed record ReevalResult(
    string Html, IReadOnlyList<long> WorthyIds, IReadOnlyList<long> QueuedJobIds, string? StoppedReason);

/// <summary>
/// The verdict-improvement sweep. Three stages, cost scaling with certainty:
/// 0) free heuristics over ALL researched ideas (missed reasoning upgrades, open
///    questions, verdict-vs-score tension, late notes, shallow-flagged appeals);
/// 1) ONE batched nano call screens the shortlist into rerun/targeted/leave;
/// 2) top picks become durable research jobs - and re-research builds ON TOP of the
///    previous report (injected context), never from scratch.
/// </summary>
public sealed class ReevalService(
    IdeaEngineDbContext db,
    OpenRouterChatClient chat,
    BudgetGuard budgetGuard,
    JobService jobs,
    IAdviceJournal adviceJournal,
    TimeProvider timeProvider,
    IOptions<ReevalOptions> reevalOptions,
    IOptions<GlanceOptions> nanoOptions,
    ILogger<ReevalService> logger)
{
    private const string StageName = "reeval";

    private const string ScreenSystem =
        """
        You screen previously-researched product ideas for RE-evaluation worthiness. Each line
        describes an idea, its old verdict, and what has changed since (engine upgrades it never
        saw, unresolved questions, operator objections). Reply with ONLY a JSON object:
        {"evals":[{"id":123,"worth":0.0-1.0,"mode":"rerun|targeted|leave","reason":"one concrete line"}]}
        Rules:
        - "rerun" = full re-research justified (verdict likely wrong or made by a much weaker
          process); "targeted" = only the open questions/objections need closing; "leave" = the
          verdict stands, changes since are cosmetic.
        - Missing several reasoning upgrades (debate, arbitrage valuation, page-reading) matters
          MOST for kills with decent scores - those were judged by a harsher, blinder process.
        - "NEVER RESEARCHED" lines: worth = should it enter research AT ALL under today's
          philosophy (individual judgment, low floor) - age and operator origin raise urgency.
        - Be honest: most ideas deserve "leave". Re-research costs real money.
        """;

    public async Task<ReevalResult> RunAsync(IProgressHandle? progress, CancellationToken cancellationToken)
    {
        var options = reevalOptions.Value;
        if (!options.Enabled || !chat.IsConfigured)
        {
            return new ReevalResult(string.Empty, [], [], "reeval disabled or OPENROUTER_API_KEY missing");
        }

        var now = timeProvider.GetUtcNow();

        // ---- Stage 0: free heuristics over every researched idea. ----
        if (progress is not null)
        {
            await progress.UpdateAsync("1/3 scanning all researched ideas (free heuristics)…", cancellationToken);
        }

        var latestReports = (await db.ResearchReports
                .OrderByDescending(r => r.Id)
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.IdeaId)
            .Select(g => g.First())
            .ToList();

        var ideaIds = latestReports.Select(r => r.IdeaId).ToList();
        var ideas = await db.Ideas
            .Where(i => ideaIds.Contains(i.Id) && i.Category != "meta")
            .ToDictionaryAsync(i => i.Id, cancellationToken);

        var snapshots = new List<(ReevalSnapshot Snapshot, double Priority, IReadOnlyList<string> Reasons, string? EngineVersion)>();
        foreach (var report in latestReports)
        {
            if (!ideas.TryGetValue(report.IdeaId, out var idea) || idea.Status == "hot" || idea.Verified)
            {
                continue; // hot = already actionable; verified = you have personally closed it
            }

            var reportDto = LlmJson.SafeDeserialize<ResearchReportDto>(report.ReportJson);
            var unified = IdeaScores.Compute(idea, reportDto);
            var openQuestions = (reportDto?.Answers ?? []).Count(a => !a.IsAnswered);
            var notes = LlmJson.SafeDeserialize<List<NoteDto>>(idea.NotesJson);
            var notesAfter = notes is { Count: > 0 } && notes.Max(n => n.At) > report.CreatedAt;
            var appeal = LlmJson.SafeDeserialize<AppealDto>(idea.AppealJson);
            var appealShallow = appeal?.Assessment is "shallow" or "unfair";

            var snapshot = new ReevalSnapshot(
                idea.Id,
                idea.Title,
                ReasoningMilestones.MissedSince(report.EngineVersion).Count,
                openQuestions,
                report.Verdict,
                unified.Total,
                notesAfter,
                appealShallow,
                !string.IsNullOrWhiteSpace(idea.RelatedJson),
                (now - report.CreatedAt).TotalDays);

            var (priority, reasons) = ReevalScoring.Score(snapshot);
            if (priority >= options.MinPriority)
            {
                snapshots.Add((snapshot, priority, reasons, report.EngineVersion));
            }
        }

        // Backlog: candidates that never reached research at all (any age) - the
        // threshold-era leftovers the owner asked about join the same funnel.
        var researchedIdSet = latestReports.Select(r => r.IdeaId).ToHashSet();
        var backlog = await db.Ideas
            .Where(i => i.Category != "meta" && i.Status == "candidate" && !i.Verified
                && !researchedIdSet.Contains(i.Id))
            .ToListAsync(cancellationToken);
        foreach (var idea in backlog)
        {
            var estimate = IdeaScores.Rating(idea);
            var snapshot = new ReevalSnapshot(
                idea.Id, idea.Title, 0, 0, "none", estimate, false, false,
                !string.IsNullOrWhiteSpace(idea.RelatedJson),
                (now - idea.CreatedAt).TotalDays);
            var (priority, reasons) = ReevalScoring.ScoreBacklog(
                estimate, (now - idea.CreatedAt).TotalDays, idea.Origin,
                !string.IsNullOrWhiteSpace(idea.RelatedJson));
            if (priority >= options.MinPriority)
            {
                snapshots.Add((snapshot, priority, reasons, null));
            }
        }

        var scanned = latestReports.Count + backlog.Count;
        if (snapshots.Count == 0)
        {
            return new ReevalResult(
                $"🔄 <b>Re-eval sweep</b>\nScanned {scanned} researched idea(s) — every verdict still stands. " +
                "Nothing missed enough upgrades or carries unresolved tension.",
                [], [], null);
        }

        var shortlist = snapshots
            .OrderByDescending(s => s.Priority)
            .Take(options.ShortlistSize)
            .ToList();

        // ---- Stage 1: one batched nano screen. ----
        if (progress is not null)
        {
            await progress.UpdateAsync(
                $"2/3 nano-screening {shortlist.Count} candidate(s) (one batched call)…", cancellationToken);
        }

        var nano = nanoOptions.Value;
        var worstCall = (4_000m * nano.InputPricePerMTok + 900 * nano.OutputPricePerMTok) / 1_000_000m;
        var check = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, worstCall, worstCall, cancellationToken);
        if (!check.Allowed)
        {
            return new ReevalResult(string.Empty, [], [], check.Reason);
        }

        var digest = new StringBuilder();
        foreach (var (snapshot, priority, reasons, engineVersion) in shortlist)
        {
            var missed = ReasoningMilestones.MissedSince(engineVersion);
            digest.Append('#').Append(snapshot.IdeaId).Append(" | ").Append(TextClip.Clip(snapshot.Title, 60))
                .Append(" | ").Append(snapshot.Verdict == "none" ? "NEVER RESEARCHED" : $"verdict {snapshot.Verdict}")
                .Append(" ⭐").Append((snapshot.UnifiedScore * 100).ToString("F0", CultureInfo.InvariantCulture))
                .Append("% | heuristic ").Append((priority * 100).ToString("F0", CultureInfo.InvariantCulture))
                .Append("% | ").Append(string.Join("; ", reasons));
            if (missed.Count > 0)
            {
                digest.Append(" | never saw: ")
                    .Append(string.Join("; ", missed.Take(3).Select(m => m.Summary)));
            }

            digest.Append('\n');
        }

        var completion = await chat.CompleteAsync(
            nano.Model, ScreenSystem, digest.ToString(), 900, "low", cancellationToken);
        if (completion is not null)
        {
            db.AiLedger.Add(new AiLedgerEntry
            {
                Day = DateOnly.FromDateTime(now.UtcDateTime),
                Stage = StageName,
                Model = nano.Model,
                TokensIn = completion.TokensIn,
                TokensOut = completion.TokensOut,
                CostUsd = (completion.TokensIn * nano.InputPricePerMTok
                    + completion.TokensOut * nano.OutputPricePerMTok) / 1_000_000m,
                CreatedAt = now,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        var evals = (LlmJson.TryParse<ScreenDto>(completion?.Content)?.Evals ?? [])
            .Where(e => shortlist.Any(s => s.Snapshot.IdeaId == e.Id))
            .ToDictionary(e => e.Id);

        // ---- Stage 2: queue the worthy (top N auto, rest one-tap). ----
        var worthy = shortlist
            .Select(s => (s.Snapshot, s.Priority,
                Eval: evals.GetValueOrDefault(s.Snapshot.IdeaId)))
            .Where(x => x.Eval is not null && x.Eval.Worth >= options.WorthThreshold && x.Eval.Mode != "leave")
            .OrderByDescending(x => x.Eval!.Worth)
            .ToList();

        if (progress is not null)
        {
            await progress.UpdateAsync(
                $"3/3 queueing top {Math.Min(options.AutoQueueTop, worthy.Count)} of {worthy.Count} worthy…",
                cancellationToken);
        }

        var queuedJobs = new List<long>();
        var builder = new StringBuilder();
        builder.Append("🔄 <b>Re-eval sweep</b>\n")
            .Append("Scanned <b>").Append(scanned).Append("</b> · shortlisted ").Append(shortlist.Count)
            .Append(" · worthy <b>").Append(worthy.Count).Append("</b>\n");

        foreach (var (snapshot, _, eval) in worthy)
        {
            var auto = queuedJobs.Count < options.AutoQueueTop;
            if (auto)
            {
                var (jobId, _) = await jobs.EnqueueAsync(
                    "research", new ResearchJobPayload(snapshot.IdeaId), null, cancellationToken);
                queuedJobs.Add(jobId);
            }

            builder.Append(auto ? "🔎 " : "• ").Append("<b>#").Append(snapshot.IdeaId).Append("</b> (")
                .Append((eval!.Worth * 100).ToString("F0", CultureInfo.InvariantCulture)).Append("% ")
                .Append(eval.Mode).Append(") ")
                .Append(WebUtility.HtmlEncode(TextClip.Clip(snapshot.Title, 45)))
                .Append("\n   <i>").Append(WebUtility.HtmlEncode(TextClip.Clip(eval.Reason ?? string.Empty, 110)))
                .Append("</i>").Append(auto ? " → <b>queued</b>" : $" — /research {snapshot.IdeaId}")
                .Append('\n');
        }

        var left = shortlist
            .Where(s => !worthy.Any(w => w.Snapshot.IdeaId == s.Snapshot.IdeaId))
            .Select(s => s.Snapshot.IdeaId)
            .ToList();
        if (left.Count > 0)
        {
            builder.Append("⏭ verdicts stand: ").Append(string.Join(' ', left.Select(id => $"#{id}"))).Append('\n');
        }

        builder.Append("<i>re-research builds ON TOP of the old report — settled findings aren't redone</i>");

        await adviceJournal.AppendAsync(
            $"## {now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC · reeval\n" +
            $"scanned={scanned} shortlist={shortlist.Count} worthy={worthy.Count} " +
            $"queued=[{string.Join(',', worthy.Take(options.AutoQueueTop).Select(w => w.Snapshot.IdeaId))}] " +
            $"flagged=[{string.Join(',', worthy.Skip(options.AutoQueueTop).Select(w => w.Snapshot.IdeaId))}]\n",
            cancellationToken);

        logger.LogInformation(
            "Reeval: scanned {Scanned}, shortlist {Shortlist}, worthy {Worthy}, queued {Queued}",
            scanned, shortlist.Count, worthy.Count, queuedJobs.Count);

        return new ReevalResult(
            builder.ToString().TrimEnd(),
            [.. worthy.Skip(options.AutoQueueTop).Select(w => w.Snapshot.IdeaId)],
            queuedJobs,
            null);
    }

    private sealed record ScreenDto([property: JsonPropertyName("evals")] IReadOnlyList<EvalDto>? Evals);

    private sealed record EvalDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("worth")] double Worth,
        [property: JsonPropertyName("mode")] string? Mode,
        [property: JsonPropertyName("reason")] string? Reason);

    private sealed record NoteDto(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("at")] DateTimeOffset At);

    private sealed record AppealDto(
        [property: JsonPropertyName("assessment")] string? Assessment);
}
