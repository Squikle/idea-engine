using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ideation;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Research;

public sealed record ResearchRunResult(
    string Html, string? Verdict, decimal CostUsd, int SearchesUsed, string? StoppedReason);

/// <summary>
/// The final validation stage: plan queries → Brave web search → grounded synthesis.
/// Verdict moves the idea: go → hot, maybe → validated, no-go → dismissed.
/// Everything is ledgered under stage "research" and budget-guarded.
/// </summary>
public sealed class ResearchService(
    IdeaEngineDbContext db,
    OpenRouterChatClient chat,
    BraveSearchClient brave,
    BudgetGuard budgetGuard,
    IStatusBoard statusBoard,
    TimeProvider timeProvider,
    IOptions<ResearchOptions> researchOptions,
    ILogger<ResearchService> logger)
{
    private const string StageName = "research";

    public async Task<ResearchRunResult> RunAsync(
        long ideaId, IProgressHandle? progress, CancellationToken cancellationToken)
    {
        var options = researchOptions.Value;
        if (!options.Enabled || !chat.IsConfigured)
        {
            return Stopped("research disabled or OPENROUTER_API_KEY missing");
        }

        if (!brave.IsConfigured)
        {
            return Stopped("BRAVE_API_KEY missing in .env");
        }

        var idea = await db.Ideas.FindAsync([ideaId], cancellationToken);
        if (idea is null)
        {
            return Stopped($"no idea #{ideaId}");
        }

        if (idea.Category == "meta")
        {
            return Stopped("meta proposals are for the operator, not web research");
        }

        var worstSynthesis = (16_000m * options.InputPricePerMTok
            + options.MaxCompletionTokens * options.OutputPricePerMTok) / 1_000_000m;
        var check = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, worstSynthesis, worstSynthesis * 1.3m, cancellationToken);
        if (!check.Allowed)
        {
            return Stopped(check.Reason);
        }

        await statusBoard.UpdateAsync(
            "Researching", $"#{idea.Id} {Truncate(idea.Title, 40)}", null, cancellationToken);

        var skeptic = SafeDeserialize<SkepticReview>(idea.SkepticJson);
        var openQuestions = (skeptic?.ResearchQuestions ?? []).Take(6).ToList();
        var evidence = await LoadEvidenceSummariesAsync(idea, cancellationToken);

        var ideaContext = ResearchPrompts.BuildIdeaContext(
            idea.Title, idea.Thesis, idea.Category, idea.TargetUser, idea.Monetization,
            idea.EffortScale, openQuestions, evidence);

        var variants = SafeDeserialize<List<string>>(idea.VariantsJson);
        if (variants is { Count: > 0 })
        {
            ideaContext += "Operator variants to evaluate: " + string.Join(" · ", variants.Take(6)) + "\n";
        }

        decimal cost = 0;

        // 1. Plan queries.
        if (progress is not null)
        {
            await progress.UpdateAsync(
                $"Research #{idea.Id} · planning web queries…", cancellationToken);
        }

        var planCompletion = await chat.CompleteAsync(
            options.Model, ResearchPrompts.PlanningSystem, ideaContext, 1500, "low", cancellationToken);
        cost += RecordLedger(planCompletion, options);

        var queries = (LlmJson.TryParse<QueriesDto>(planCompletion?.Content)?.Queries ?? [])
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(options.MaxQueries)
            .ToList();

        if (queries.Count == 0)
        {
            // Degrade gracefully: search the skeptic's questions, or the title itself.
            queries = openQuestions.Count > 0
                ? [.. openQuestions.Take(options.MaxQueries)]
                : [idea.Title];
        }

        // 2. Web search (Brave free plan: 1 req/s).
        var blocks = new List<(string Query, IReadOnlyList<SearchHit> Hits)>();
        foreach (var query in queries)
        {
            if (progress is not null)
            {
                await progress.UpdateAsync(
                    $"Research #{idea.Id} · searching {blocks.Count + 1}/{queries.Count}: {Truncate(query, 45)}…",
                    cancellationToken);
            }

            await Task.Delay(options.SearchDelayMs, cancellationToken);
            blocks.Add((query, await brave.SearchAsync(query, options.ResultsPerQuery, cancellationToken)));
        }

        var sourcesCount = blocks.Sum(b => b.Hits.Count);
        if (sourcesCount == 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            return Stopped("web search returned no results (Brave quota or connectivity?)");
        }

        // 3. Grounded synthesis (one retry on unparseable output).
        if (progress is not null)
        {
            await progress.UpdateAsync(
                $"Research #{idea.Id} · synthesizing verdict from {sourcesCount} sources…", cancellationToken);
        }

        ReportDto? report = null;
        for (var attempt = 1; attempt <= 2 && report is null; attempt++)
        {
            var synthesis = await chat.CompleteAsync(
                options.Model, ResearchPrompts.SynthesisSystem,
                ResearchPrompts.BuildSynthesisMessage(ideaContext, blocks),
                options.MaxCompletionTokens, options.ReasoningEffort, cancellationToken);
            cost += RecordLedger(synthesis, options);
            report = LlmJson.TryParse<ReportDto>(synthesis?.Content);
        }

        if (report is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return Stopped("synthesis returned unparseable output twice");
        }

        // 4. Persist + move the idea.
        var verdict = NormalizeVerdict(report.Verdict);
        db.ResearchReports.Add(new ResearchReportEntity
        {
            IdeaId = idea.Id,
            Verdict = verdict,
            Confidence = Math.Clamp(report.Confidence, 0, 1),
            ReportJson = JsonSerializer.Serialize(report, LlmJson.Options),
            QueriesJson = JsonSerializer.Serialize(queries, LlmJson.Options),
            SearchesUsed = blocks.Count,
            SourcesCount = sourcesCount,
            Model = options.Model,
            CostUsd = cost,
            CreatedAt = timeProvider.GetUtcNow(),
        });

        idea.Status = verdict switch
        {
            "go" => "hot",
            "no-go" => "dismissed",
            _ => "validated",
        };

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Research #{IdeaId}: {Verdict} (conf {Confidence:F2}), {Sources} sources, ${Cost:F4}",
            idea.Id, verdict, report.Confidence, sourcesCount, cost);

        return new ResearchRunResult(
            FormatReport(idea, verdict, report, cost, blocks.Count), verdict, cost, blocks.Count, null);
    }

    private async Task<List<string>> LoadEvidenceSummariesAsync(
        IdeaEntity idea, CancellationToken cancellationToken)
    {
        var ids = SafeDeserialize<List<long>>(idea.EvidenceJson) ?? [];
        if (ids.Count == 0)
        {
            return [];
        }

        return await db.Signals
            .Where(s => ids.Contains(s.Id))
            .OrderByDescending(s => s.Confidence)
            .Take(5)
            .Select(s => s.Summary)
            .ToListAsync(cancellationToken);
    }

    private decimal RecordLedger(ChatCompletion? completion, ResearchOptions options)
    {
        if (completion is null)
        {
            return 0;
        }

        var cost = (completion.TokensIn * options.InputPricePerMTok
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

        return cost;
    }

    private static string FormatReport(
        IdeaEntity idea, string verdict, ReportDto report, decimal cost, int searches)
    {
        var builder = new StringBuilder();
        builder.Append(Ui.Research).Append(" <b>Research #").Append(idea.Id).Append(" · ")
            .Append(WebUtility.HtmlEncode(idea.Title)).Append("</b>\n")
            .Append("Verdict: <b>").Append(Ui.Verdict(verdict)).Append("</b> (conf ")
            .Append(report.Confidence.ToString("F2", CultureInfo.InvariantCulture))
            .Append(") → status ").Append(idea.Status).Append('\n');

        var competitors = (report.Competitors ?? []).Take(4).ToList();
        if (competitors.Count > 0)
        {
            builder.Append("\n<b>🏪 Competitors</b>\n");
            foreach (var competitor in competitors)
            {
                builder.Append("• ");
                if (competitor.Url is { Length: > 0 })
                {
                    builder.Append("<a href=\"").Append(competitor.Url).Append("\">")
                        .Append(WebUtility.HtmlEncode(competitor.Name ?? "?")).Append("</a>");
                }
                else
                {
                    builder.Append(WebUtility.HtmlEncode(competitor.Name ?? "?"));
                }

                builder.Append(" — ").Append(WebUtility.HtmlEncode(Truncate(competitor.Why ?? string.Empty, 110)))
                    .Append('\n');
            }
        }

        foreach (var answer in (report.Answers ?? []).Take(3))
        {
            builder.Append("\n? ").Append(WebUtility.HtmlEncode(Truncate(answer.Question ?? string.Empty, 100)))
                .Append('\n').Append(WebUtility.HtmlEncode(Truncate(answer.Answer ?? string.Empty, 220))).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(report.DifferentiationPath))
        {
            builder.Append("\n<b>🧭 Differentiation:</b> ")
                .Append(WebUtility.HtmlEncode(Truncate(report.DifferentiationPath, 250))).Append('\n');
        }

        var risks = (report.Risks ?? []).Take(3).ToList();
        if (risks.Count > 0)
        {
            builder.Append("<b>⚠️ Risks:</b> ")
                .Append(WebUtility.HtmlEncode(Truncate(string.Join("; ", risks), 280))).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(report.MvpTest))
        {
            builder.Append("<b>🧪 MVP test:</b> ")
                .Append(WebUtility.HtmlEncode(Truncate(report.MvpTest, 220))).Append('\n');
        }

        var variants = (report.RelatedVariants ?? []).Take(4).ToList();
        if (variants.Count > 0)
        {
            builder.Append("<b>🔀 Stronger variants:</b> ")
                .Append(WebUtility.HtmlEncode(Truncate(string.Join(" · ", variants), 260))).Append('\n');
        }

        var steps = (report.NextSteps ?? []).Take(3).ToList();
        if (steps.Count > 0)
        {
            builder.Append("<b>➡️ Next steps</b>\n");
            foreach (var step in steps)
            {
                builder.Append("→ ").Append(WebUtility.HtmlEncode(Truncate(step, 140))).Append('\n');
            }
        }

        builder.Append('\n').Append(Ui.Spend).Append(" $").Append(cost.ToString("F4", CultureInfo.InvariantCulture))
            .Append(" · ").Append(searches).Append(" searches · /idea ").Append(idea.Id);

        var text = builder.ToString().TrimEnd();
        return text.Length <= 3900 ? text : text[..3900] + "…";
    }

    private static string NormalizeVerdict(string? verdict) =>
        verdict?.Trim().ToLowerInvariant() switch
        {
            "go" => "go",
            "no-go" or "nogo" or "no_go" => "no-go",
            _ => "maybe",
        };

    private static ResearchRunResult Stopped(string? reason) => new(string.Empty, null, 0, 0, reason);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static T? SafeDeserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, LlmJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record QueriesDto(
        [property: JsonPropertyName("queries")] IReadOnlyList<string>? Queries);

    private sealed record ReportDto(
        [property: JsonPropertyName("verdict")] string? Verdict,
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("competitors")] IReadOnlyList<CompetitorDto>? Competitors,
        [property: JsonPropertyName("answers")] IReadOnlyList<AnswerDto>? Answers,
        [property: JsonPropertyName("market_notes")] string? MarketNotes,
        [property: JsonPropertyName("differentiation_path")] string? DifferentiationPath,
        [property: JsonPropertyName("risks")] IReadOnlyList<string>? Risks,
        [property: JsonPropertyName("mvp_test")] string? MvpTest,
        [property: JsonPropertyName("related_variants")] IReadOnlyList<string>? RelatedVariants,
        [property: JsonPropertyName("next_steps")] IReadOnlyList<string>? NextSteps,
        [property: JsonPropertyName("scores")] Dictionary<string, double>? Scores);

    private sealed record CompetitorDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("why")] string? Why);

    private sealed record AnswerDto(
        [property: JsonPropertyName("question")] string? Question,
        [property: JsonPropertyName("answer")] string? Answer,
        [property: JsonPropertyName("evidence_urls")] IReadOnlyList<string>? EvidenceUrls);
}
