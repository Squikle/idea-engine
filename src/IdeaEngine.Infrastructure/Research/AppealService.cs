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
}

public sealed record AppealResult(string Html, string? NewStatus, decimal CostUsd, string? StoppedReason);

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
    ILogger<AppealService> logger)
{
    private const string StageName = "appeal";

    private const string SystemPrompt =
        """
        You are the court of appeal for startup idea verdicts. You receive an idea, the
        skeptic's pre-research review, the researcher's evidence-based report, and the
        operator's notes. Judge the JUDGMENT, not the idea from scratch: was the verdict
        justified by the evidence shown? Was anything important ignored, misweighed, or
        asserted without evidence? Reply with ONLY a JSON object:
        {"assessment":"fair|shallow|unfair","missed":["important points the verdict ignored"],
         "overturn":null,"new_verdict":"go|maybe|no-go or null when upholding",
         "justification":"2-4 sentences, concrete","confidence":0.0-1.0}
        Rules:
        - Uphold verdicts that are evidence-grounded even if debatable; overturn only for
          clear misjudgment (ignored evidence, wrong-goal judging, unsupported fatal flaw).
        - Garage-scale lens applies (1-3 people). Do not moralize; edgy niches are valid.
        - Reputation/content-category ideas are judged on audience/virality, not revenue.
        """;

    public async Task<AppealResult> RunAsync(long ideaId, CancellationToken cancellationToken)
    {
        var options = appealOptions.Value;
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
        if (report is null)
        {
            return new AppealResult(string.Empty, null, 0, "nothing to appeal - no research report yet (/research first)");
        }

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
            .Append("Status after research: ").Append(idea.Status).Append('\n')
            .Append("Thesis: ").Append(idea.Thesis).Append('\n');
        if (idea.SkepticJson is { Length: > 0 })
        {
            builder.Append("\nSkeptic (pre-research):\n").Append(idea.SkepticJson).Append('\n');
        }

        builder.Append("\nResearch verdict: ").Append(report.Verdict)
            .Append(" (confidence ").Append(report.Confidence.ToString("F2", CultureInfo.InvariantCulture))
            .Append(")\nResearch report:\n").Append(report.ReportJson).Append('\n');

        if (idea.NotesJson is { Length: > 0 })
        {
            builder.Append("\nOperator notes:\n").Append(idea.NotesJson).Append('\n');
        }

        var completion = await chat.CompleteAsync(
            options.Model, SystemPrompt, builder.ToString(),
            options.MaxCompletionTokens, "medium", cancellationToken);

        decimal cost = 0;
        if (completion is not null)
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
            return new AppealResult(string.Empty, null, cost, "appeal output unparseable");
        }

        string? newStatus = null;
        var overturned = verdict.NewVerdict is { Length: > 0 } nv
            && !string.Equals(nv, report.Verdict, StringComparison.OrdinalIgnoreCase);
        if (overturned)
        {
            newStatus = verdict.NewVerdict!.ToLowerInvariant() switch
            {
                "go" => "hot",
                "no-go" or "nogo" => "dismissed",
                _ => "uncertain",
            };
            idea.Status = newStatus;
            idea.Verified = false; // overturned ideas return to the review inbox
        }

        idea.AppealJson = JsonSerializer.Serialize(verdict, LlmJson.Options);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Appeal #{IdeaId}: {Assessment}, overturn={Overturned}, ${Cost:F4}",
            ideaId, verdict.Assessment, overturned, cost);

        var html = new StringBuilder();
        html.Append("⚖️ <b>Appeal #").Append(idea.Id).Append(" · ")
            .Append(WebUtility.HtmlEncode(TextClip.Clip(idea.Title, 60))).Append("</b>\n")
            .Append("Judgment was: <b>").Append(verdict.Assessment ?? "?").Append("</b>");
        html.Append(overturned
            ? $" → overturned to {Ui.Verdict(verdict.NewVerdict)} · status {newStatus}\n"
            : " → verdict upheld\n");

        foreach (var missed in (verdict.Missed ?? []).Take(3))
        {
            html.Append("• ").Append(WebUtility.HtmlEncode(TextClip.Clip(missed, 130))).Append('\n');
        }

        if (verdict.Justification is { Length: > 0 })
        {
            html.Append("<i>").Append(WebUtility.HtmlEncode(TextClip.Clip(verdict.Justification, 300))).Append("</i>\n");
        }

        html.Append(Ui.Spend).Append(" $").Append(cost.ToString("F3", CultureInfo.InvariantCulture))
            .Append(" · /idea ").Append(idea.Id);

        return new AppealResult(html.ToString(), newStatus, cost, null);
    }

    private sealed record AppealDto(
        [property: JsonPropertyName("assessment")] string? Assessment,
        [property: JsonPropertyName("missed")] IReadOnlyList<string>? Missed,
        [property: JsonPropertyName("new_verdict")] string? NewVerdict,
        [property: JsonPropertyName("justification")] string? Justification,
        [property: JsonPropertyName("confidence")] double Confidence);
}
