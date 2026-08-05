using System.Globalization;
using System.Text;
using System.Text.Json;
using IdeaEngine.Core.Common;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ideation;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Research;

/// <summary>Bound from configuration section <c>IdeaEngine:Ai:Partner</c>.</summary>
public sealed class PartnerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>The judgment seat: strongest model on purpose (swappable via /models).</summary>
    public string Model { get; set; } = "anthropic/claude-opus-4.6";

    public decimal InputPricePerMTok { get; set; } = 5.00m;

    public decimal OutputPricePerMTok { get; set; } = 25.00m;

    public decimal DailyUsdCap { get; set; } = 2.00m;

    public int MaxCompletionTokens { get; set; } = 1200;

    public string ReasoningEffort { get; set; } = "medium";

    public PartnerOptions WithModel(ResolvedModel resolved)
    {
        var clone = (PartnerOptions)MemberwiseClone();
        clone.Model = resolved.Model;
        clone.InputPricePerMTok = resolved.InPerMTok;
        clone.OutputPricePerMTok = resolved.OutPerMTok;
        clone.ReasoningEffort = resolved.Effort ?? clone.ReasoningEffort;
        return clone;
    }
}

public sealed record PartnerResult(string Html, decimal CostUsd, string? StoppedReason);

/// <summary>
/// The operator's right-hand take: reads the full judgment trail, compares against the
/// portfolio, and answers like an experienced partner would - in ten blunt lines, not a
/// report. Exists because percents don't show everything and walls of text don't get read.
/// </summary>
public sealed class PartnerService(
    IdeaEngineDbContext db,
    OpenRouterChatClient chat,
    BudgetGuard budgetGuard,
    TimeProvider timeProvider,
    IOptions<PartnerOptions> partnerOptions,
    ModelRegistry models,
    ILogger<PartnerService> logger)
{
    private const string StageName = "partner";

    private const string SystemPrompt =
        """
        You are the operator's technical co-founder and closest advisor - blunt, warm,
        experienced, zero corporate speak. He is a solo developer building a PORTFOLIO of
        small products in evenings with AI coding tools; a 2-day build earning $100/month
        forever is a real win, and his time is the scarcest resource.
        You receive one idea's full judgment trail (skeptic, research with concern ledger,
        appeal, his own notes) plus a compressed portfolio snapshot for comparison.
        Answer in AT MOST 10 short lines, plain human language (contractions fine, no
        headers, no bullets-of-bullets):
        1) your gut take in one sentence;
        2) effort vs payoff for HIM specifically (days to MVP, realistic monthly ceiling);
        3) how it ranks against his other ideas (name 1-2 by number when relevant);
        4) the one thing that would change your mind;
        5) final word: BUILD / PARK / FIX FIRST / KILL - and if the AIs already got it
           right, say so plainly ("the skeptic's right on this one, let it go").
        Be honest over kind. Never invent evidence; when the trail is thin, say so.
        No moralizing; edgy niches are valid business.
        """;

    public async Task<PartnerResult> RunAsync(long ideaId, CancellationToken cancellationToken)
    {
        var baseOptions = partnerOptions.Value;
        var options = baseOptions.WithModel(await models.ResolveAsync(
            StageName, baseOptions.Model, baseOptions.InputPricePerMTok,
            baseOptions.OutputPricePerMTok, cancellationToken));
        if (!options.Enabled || !chat.IsConfigured)
        {
            return new PartnerResult(string.Empty, 0, "partner disabled or OPENROUTER_API_KEY missing");
        }

        var idea = await db.Ideas.FindAsync([ideaId], cancellationToken);
        if (idea is null)
        {
            return new PartnerResult(string.Empty, 0, $"no idea #{ideaId}");
        }

        var worstCall = (14_000m * options.InputPricePerMTok
            + options.MaxCompletionTokens * options.OutputPricePerMTok) / 1_000_000m;
        var check = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, worstCall, worstCall, cancellationToken);
        if (!check.Allowed)
        {
            return new PartnerResult(string.Empty, 0, check.Reason);
        }

        var report = await db.ResearchReports
            .Where(r => r.IdeaId == ideaId)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var context = await BuildContextAsync(idea, report, cancellationToken);
        var completion = await chat.CompleteAsync(
            options.Model, SystemPrompt, context, options.MaxCompletionTokens, options.ReasoningEffort, cancellationToken);

        decimal cost = 0;
        if (completion is { IsError: false })
        {
            cost = (completion.TokensIn * options.InputPricePerMTok
                + completion.TokensOut * options.OutputPricePerMTok) / 1_000_000m;
            db.AiLedger.Add(new AiLedgerEntry
            {
                Day = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime),
                Stage = StageName,
                Model = options.Model,
                TokensIn = completion.TokensIn,
                TokensOut = completion.TokensOut,
                CostUsd = cost,
                CreatedAt = timeProvider.GetUtcNow(),
            });
        }

        if (completion is null || completion.IsError || string.IsNullOrWhiteSpace(completion.Content))
        {
            await db.SaveChangesAsync(cancellationToken);
            return new PartnerResult(string.Empty, cost, $"partner failed — {LlmDiag.Describe(completion)}");
        }

        var take = completion.Content.Trim();
        idea.PartnerJson = JsonSerializer.Serialize(new
        {
            text = take,
            at = timeProvider.GetUtcNow(),
            model = options.Model,
        }, LlmJson.Options);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Partner take #{IdeaId}: ${Cost:F4}", ideaId, cost);

        var html = new StringBuilder();
        html.Append("🤝 <b>Partner take · #").Append(idea.Id).Append(" · ")
            .Append(System.Net.WebUtility.HtmlEncode(TextClip.Clip(idea.Title, 60))).Append("</b>\n")
            .Append(System.Net.WebUtility.HtmlEncode(take)).Append('\n')
            .Append(Ui.Spend).Append(" $").Append(cost.ToString("F3", CultureInfo.InvariantCulture))
            .Append(" · ").Append(options.Model).Append(" · ").Append(Ui.Cmd("idea", idea.Id));
        return new PartnerResult(html.ToString(), cost, null);
    }

    private async Task<string> BuildContextAsync(
        IdeaEntity idea, ResearchReportEntity? report, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append("IDEA #").Append(idea.Id).Append(": ").Append(idea.Title).Append('\n')
            .Append("Status: ").Append(idea.Status).Append(" · category ").Append(idea.Category)
            .Append(" · effort ").Append(idea.EffortScale).Append("/5 · origin ").Append(idea.Origin).Append('\n')
            .Append("Thesis: ").Append(idea.Thesis).Append('\n');
        if (idea.OriginalPitch is { Length: > 0 })
        {
            builder.Append("His original pitch (verbatim): ").Append(idea.OriginalPitch).Append('\n');
        }

        if (idea.TargetUser is { Length: > 0 })
        {
            builder.Append("Target: ").Append(idea.TargetUser).Append('\n');
        }

        if (idea.Monetization is { Length: > 0 })
        {
            builder.Append("Monetization: ").Append(idea.Monetization).Append('\n');
        }

        if (idea.SkepticJson is { Length: > 0 })
        {
            builder.Append("\nSKEPTIC:\n").Append(idea.SkepticJson).Append('\n');
        }

        if (report is not null)
        {
            builder.Append("\nLATEST RESEARCH (verdict ").Append(report.Verdict)
                .Append(", confidence ").Append(report.Confidence.ToString("F2", CultureInfo.InvariantCulture))
                .Append(", engine v").Append(report.EngineVersion ?? "old").Append("):\n")
                .Append(report.ReportJson).Append('\n');
        }
        else
        {
            builder.Append("\nNO RESEARCH YET - only the skeptic has spoken.\n");
        }

        if (idea.AppealJson is { Length: > 0 })
        {
            builder.Append("\nCOURT OF APPEAL:\n").Append(idea.AppealJson).Append('\n');
        }

        if (idea.NotesJson is { Length: > 0 })
        {
            builder.Append("\nHIS NOTES:\n").Append(idea.NotesJson).Append('\n');
        }

        // Portfolio snapshot: one-liners only - the partner compares, it doesn't re-read everything.
        var others = await db.Ideas
            .Where(i => i.Id != idea.Id && i.Category != "meta" && i.Status != "dismissed")
            .ToListAsync(cancellationToken);
        var rated = others
            .Select(i => (i.Id, i.Title, i.Status, Rating: IdeaScores.Rating(i)))
            .OrderByDescending(x => x.Rating)
            .Take(12)
            .ToList();

        builder.Append("\nPORTFOLIO SNAPSHOT (top by current rating; ≈ pre-research estimates):\n");
        foreach (var (id, title, status, rating) in rated)
        {
            builder.Append("- #").Append(id).Append(" [").Append(status).Append("] ≈")
                .Append((rating * 100).ToString("F0", CultureInfo.InvariantCulture)).Append("% ")
                .Append(TextClip.Clip(title, 70)).Append('\n');
        }

        var related = LlmJson.SafeDeserialize<List<Dictionary<string, JsonElement>>>(idea.RelatedJson);
        if (related is { Count: > 0 })
        {
            builder.Append("\nRELATED IDEAS (linked): ")
                .Append(string.Join(", ", related.Select(r =>
                    r.TryGetValue("id", out var idEl) ? "#" + idEl.ToString() : "?")))
                .Append('\n');
        }

        return builder.ToString();
    }
}
