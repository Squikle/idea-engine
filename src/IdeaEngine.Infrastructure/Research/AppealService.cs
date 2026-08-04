using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Common;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Research;

/// <summary>Bound from configuration section <c>IdeaEngine:Ai:Appeal</c>.</summary>
public sealed class AppealOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>A stronger model than the researcher, on purpose - it reviews the judge.</summary>
    public string Model { get; set; } = "anthropic/claude-opus-4.6";

    public decimal InputPricePerMTok { get; set; } = 5.00m;

    public decimal OutputPricePerMTok { get; set; } = 25.00m;

    public decimal DailyUsdCap { get; set; } = 1.50m;

    public int MaxCompletionTokens { get; set; } = 3000;

    /// <summary>Auto-appeal fires when a no-go verdict carries a score at/above this.</summary>
    public double AutoAppealMinScore { get; set; } = 0.5;

    /// <summary>Copy with a runtime model override applied.</summary>
    public AppealOptions WithModel(ResolvedModel resolved)
    {
        var clone = (AppealOptions)MemberwiseClone();
        clone.Model = resolved.Model;
        clone.InputPricePerMTok = resolved.InPerMTok;
        clone.OutputPricePerMTok = resolved.OutPerMTok;
        return clone;
    }
}

public sealed record AppealResult(string Html, string? NewStatus, decimal CostUsd, string? StoppedReason, bool Overturned = false);

/// <summary>
/// The court of appeal: a stronger model reviews verdict-vs-evidence for depth and
/// fairness and may overturn the status. Runs on demand (/appeal) and automatically
/// for suspicious kills (high ingredient score + no-go).
/// </summary>
public sealed class AppealService(
    IdeaEngineDbContext db,
    OpenRouterChatClient chat,
    BudgetGuard budgetGuard,
    TimeProvider timeProvider,
    IOptions<AppealOptions> appealOptions,
    ModelRegistry models,
    ILogger<AppealService> logger)
{
    private const string StageName = "appeal";

    private const string SystemPrompt =
        """
        You are the court of appeal for startup idea verdicts, and a working part of a
        DISTILLATION process: every pass must leave the idea sharper, never just stamped.
        You receive an idea, the skeptic's pre-research review, the researcher's report
        with its concern ledger (when research ran), and the operator's notes.
        Judge the JUDGMENT: was the verdict AND scoring justified by the evidence shown?
        Reply with ONLY a JSON object:
        {"assessment":"fair|shallow|unfair",
         "missed":["important points the verdict ignored"],
         "new_verdict":"go|maybe|no-go, or null when the verdict category stands",
         "score_adjustments":{"demand":0.55} or null - ONLY categories you are confident were
          misjudged given the evidence (keys: demand, willingness_to_pay, feasibility_solo,
          competition_gap), with corrected values,
         "concern_updates":[{"concern":"quote it","new_status":"open|mitigated|fatal|waived","reason":"..."}],
         "what_would_move_it":"REQUIRED ALWAYS: the cheapest concrete action or evidence that
          would raise this idea - or exactly 'nothing: <why it is hopeless>'",
         "justification":"2-4 sentences, concrete","confidence":0.0-1.0}
        Rules:
        - Uphold verdicts that are evidence-grounded even if debatable; overturn only for
          clear misjudgment (ignored evidence, wrong-goal judging, unsupported fatal flaw).
        - "fair" is NOT a dead end. Even when upholding, adjust misjudged score categories
          (cite the evidence in justification) and update concern statuses the judge got
          wrong - especially concerns the operator's notes or advocate's mitigations resolved.
        - what_would_move_it is the distillation engine: be concrete (an experiment, a pivot,
          a piece of evidence, a niche switch). Only say 'nothing: ...' when genuinely hopeless.
        - Garage-scale lens applies (1-3 people). Do not moralize; edgy niches are valid.
        - Reputation/content-category ideas are judged on audience/virality, not revenue.
        - A small niche with real willingness to pay beats a huge market with none.
        """;

    public async Task<AppealResult> RunAsync(long ideaId, CancellationToken cancellationToken)
    {
        var options = appealOptions.Value.WithModel(
            await models.ResolveAsync("appeal", appealOptions.Value.Model,
                appealOptions.Value.InputPricePerMTok, appealOptions.Value.OutputPricePerMTok, cancellationToken));
        if (!options.Enabled || !chat.IsConfigured)
        {
            return new AppealResult(string.Empty, null, 0, "appeal disabled or OPENROUTER_API_KEY missing");
        }

        var idea = await db.Ideas.FindAsync([ideaId], cancellationToken);
        if (idea is null)
        {
            return new AppealResult(string.Empty, null, 0, $"no idea #{ideaId}");
        }

        var report = await db.ResearchReports
            .Where(r => r.IdeaId == ideaId)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var worstCall = (20_000m * options.InputPricePerMTok
            + options.MaxCompletionTokens * options.OutputPricePerMTok) / 1_000_000m;
        var check = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, worstCall, worstCall, cancellationToken);
        if (!check.Allowed)
        {
            return new AppealResult(string.Empty, null, 0, check.Reason);
        }

        var builder = new StringBuilder();
        builder.Append("Idea #").Append(idea.Id).Append(": ").Append(idea.Title).Append('\n')
            .Append("Current status: ").Append(idea.Status).Append('\n')
            .Append("Thesis: ").Append(idea.Thesis).Append('\n');
        if (idea.OriginalPitch is { Length: > 0 })
        {
            builder.Append("\nOperator's raw pitch (verbatim, before AI shaping):\n")
                .Append(idea.OriginalPitch).Append('\n');
        }

        if (idea.SkepticJson is { Length: > 0 })
        {
            builder.Append("\nSkeptic (pre-research):\n").Append(idea.SkepticJson).Append('\n');
        }

        if (report is not null)
        {
            builder.Append("\nResearch verdict: ").Append(report.Verdict)
                .Append(" (confidence ").Append(report.Confidence.ToString("F2", CultureInfo.InvariantCulture))
                .Append(")\nResearch report:\n").Append(report.ReportJson).Append('\n');
        }
        else
        {
            builder.Append("\nNO RESEARCH WAS RUN. The idea was killed pre-research by the skeptic ")
                .Append("alone. Judge whether that kill is justified from the pitch and the skeptic's ")
                .Append("reasoning: a kill WITHOUT evidence should stand only for self-evidently fatal ")
                .Append("flaws (impossible economics, no conceivable user, already-free commodity). ")
                .Append("Doubt is a reason to research, not to kill.\n");
        }

        if (idea.NotesJson is { Length: > 0 })
        {
            builder.Append("\nOperator notes:\n").Append(idea.NotesJson).Append('\n');
        }

        var completion = await chat.CompleteAsync(
            options.Model, SystemPrompt, builder.ToString(),
            options.MaxCompletionTokens, "medium", cancellationToken);

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

        var verdict = LlmJson.TryParse<AppealDto>(completion?.Content);
        if (verdict is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new AppealResult(string.Empty, null, cost, $"appeal failed — {LlmDiag.Describe(completion)}");
        }

        string? newStatus = null;
        var priorVerdict = report?.Verdict ?? "no-go"; // a pre-research kill is a no-go
        var overturned = verdict.NewVerdict is { Length: > 0 } nv
            && !string.Equals(nv, priorVerdict, StringComparison.OrdinalIgnoreCase);
        if (overturned)
        {
            newStatus = verdict.NewVerdict!.ToLowerInvariant() switch
            {
                // Without research a revived idea is a candidate, not hot - evidence first.
                "go" => report is null ? "candidate" : "hot",
                "no-go" or "nogo" => "dismissed",
                _ => report is null ? "candidate" : "uncertain",
            };
            idea.Status = newStatus;
            idea.Verified = false; // overturned ideas return to the review inbox
        }

        // Persist with timestamp + engine version: staleness detection and the
        // research context loop both need to know WHEN the court last spoke.
        var appealNode = JsonSerializer.SerializeToNode(verdict, LlmJson.Options)!.AsObject();
        appealNode["at"] = timeProvider.GetUtcNow().ToString("O");
        appealNode["engine"] = typeof(AppealService).Assembly.GetName().Version?.ToString(3);
        idea.AppealJson = appealNode.ToJsonString(LlmJson.Options);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Appeal #{IdeaId}: {Assessment}, overturn={Overturned}, ${Cost:F4}",
            ideaId, verdict.Assessment, overturned, cost);

        var html = new StringBuilder();
        html.Append("⚖️ <b>Appeal #").Append(idea.Id).Append(" · ")
            .Append(WebUtility.HtmlEncode(TextClip.Clip(idea.Title, 60))).Append("</b>")
            .Append(report is null ? " <i>(pre-research kill)</i>\n" : "\n")
            .Append("Judgment was: <b>").Append(verdict.Assessment ?? "?").Append("</b>");
        html.Append(overturned
            ? $" → overturned to {Ui.Verdict(verdict.NewVerdict)} · status {newStatus}\n"
            : " → verdict upheld\n");

        // Full arguments, never clipped - the notifier splits long messages safely.
        foreach (var missed in verdict.Missed ?? [])
        {
            html.Append("• ").Append(WebUtility.HtmlEncode(missed)).Append('\n');
        }

        if (verdict.ScoreAdjustments is { Count: > 0 } adjustments)
        {
            html.Append("📐 <b>Scores corrected:</b> ").Append(string.Join(" · ",
                adjustments.Select(a => $"{a.Key} → {(a.Value * 100):F0}%"))).Append('\n');
        }

        foreach (var update in verdict.ConcernUpdates ?? [])
        {
            html.Append("🧾 ").Append(Ui.ConcernStatus(update.NewStatus)).Append(' ')
                .Append(WebUtility.HtmlEncode(update.Concern ?? string.Empty))
                .Append(" — <i>").Append(WebUtility.HtmlEncode(update.Reason ?? string.Empty)).Append("</i>\n");
        }

        if (verdict.Justification is { Length: > 0 })
        {
            html.Append("<i>").Append(WebUtility.HtmlEncode(verdict.Justification)).Append("</i>\n");
        }

        if (verdict.WhatWouldMoveIt is { Length: > 0 })
        {
            html.Append("🧭 <b>What would move it:</b> ")
                .Append(WebUtility.HtmlEncode(verdict.WhatWouldMoveIt)).Append('\n');
        }

        html.Append(Ui.Spend).Append(" $").Append(cost.ToString("F3", CultureInfo.InvariantCulture))
            .Append(" · ").Append(Ui.Cmd("idea", idea.Id)).Append(" · ").Append(Ui.Cmd("research", idea.Id))
            .Append(" digests this appeal");

        return new AppealResult(html.ToString(), newStatus, cost, null, overturned);
    }

    private sealed record AppealDto(
        [property: JsonPropertyName("assessment")] string? Assessment,
        [property: JsonPropertyName("missed")] IReadOnlyList<string>? Missed,
        [property: JsonPropertyName("new_verdict")] string? NewVerdict,
        [property: JsonPropertyName("score_adjustments")] Dictionary<string, double>? ScoreAdjustments,
        [property: JsonPropertyName("concern_updates")] IReadOnlyList<ConcernUpdateDto>? ConcernUpdates,
        [property: JsonPropertyName("what_would_move_it")] string? WhatWouldMoveIt,
        [property: JsonPropertyName("justification")] string? Justification,
        [property: JsonPropertyName("confidence")] double Confidence);

    private sealed record ConcernUpdateDto(
        [property: JsonPropertyName("concern")] string? Concern,
        [property: JsonPropertyName("new_status")] string? NewStatus,
        [property: JsonPropertyName("reason")] string? Reason);
}
