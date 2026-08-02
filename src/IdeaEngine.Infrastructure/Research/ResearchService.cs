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
/// Closure-driven validation: plan queries → search → synthesize → and while questions
/// remain unanswered, run follow-up rounds that search those exact questions AND read the
/// top result pages (snippets prove existence; pages answer pricing/features). Stops at
/// closure or MaxRounds; remaining gaps are reported honestly, never papered over.
/// </summary>
public sealed class ResearchService(
    IdeaEngineDbContext db,
    OpenRouterChatClient chat,
    BraveSearchClient brave,
    PageFetcher pageFetcher,
    BudgetGuard budgetGuard,
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

        var worstSynthesis = (40_000m * options.InputPricePerMTok
            + options.MaxCompletionTokens * options.OutputPricePerMTok) / 1_000_000m;
        var check = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, worstSynthesis,
            worstSynthesis * options.MaxRounds, cancellationToken);
        if (!check.Allowed)
        {
            return Stopped(check.Reason);
        }

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
        var searchesUsed = 0;
        var pagesRead = 0;
        var blocks = new List<(string Query, IReadOnlyList<SearchHit> Hits)>();
        var pageExcerpts = new List<(string Url, string Excerpt)>();

        // Round 1 queries: planned by the model from the idea + open questions.
        if (progress is not null)
        {
            await progress.UpdateAsync($"Research #{idea.Id} · planning web queries…", cancellationToken);
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
            queries = openQuestions.Count > 0
                ? [.. openQuestions.Take(options.MaxQueries)]
                : [idea.Title];
        }

        searchesUsed += await SearchIntoAsync(blocks, queries, idea.Id, progress, options, cancellationToken);
        if (blocks.Sum(b => b.Hits.Count) == 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            return Stopped("web search returned no results (Brave quota or connectivity?)");
        }

        // Synthesis rounds until closure or MaxRounds.
        ResearchReportDto? report = null;
        var rounds = 0;
        while (rounds < Math.Max(1, options.MaxRounds))
        {
            rounds++;
            if (progress is not null)
            {
                await progress.UpdateAsync(
                    $"Research #{idea.Id} · round {rounds}: synthesizing from " +
                    $"{blocks.Sum(b => b.Hits.Count)} snippets + {pageExcerpts.Count} pages…",
                    cancellationToken);
            }

            report = await SynthesizeAsync(ideaContext, blocks, pageExcerpts, options, c => cost += c, cancellationToken);
            if (report is null)
            {
                break;
            }

            var unanswered = UnansweredQuestions(report, openQuestions);
            if (unanswered.Count == 0 || rounds >= options.MaxRounds)
            {
                break;
            }

            var roundCheck = await budgetGuard.CheckAsync(
                StageName, options.DailyUsdCap, worstSynthesis, worstSynthesis, cancellationToken);
            if (!roundCheck.Allowed)
            {
                logger.LogInformation("Research follow-up skipped: {Reason}", roundCheck.Reason);
                break;
            }

            // Follow-up: search the unanswered questions verbatim and READ top pages.
            if (progress is not null)
            {
                await progress.UpdateAsync(
                    $"Research #{idea.Id} · digging into {unanswered.Count} open questions…",
                    cancellationToken);
            }

            var followUps = unanswered.Take(4).Select(q => Truncate(q, 140)).ToList();
            searchesUsed += await SearchIntoAsync(blocks, followUps, idea.Id, progress, options, cancellationToken);

            foreach (var question in followUps)
            {
                var hits = blocks.Last(b => b.Query == question).Hits;
                foreach (var hit in hits.Take(options.PagesPerQuestion))
                {
                    if (progress is not null)
                    {
                        await progress.UpdateAsync(
                            $"Research #{idea.Id} · reading {Truncate(hit.Url, 60)}…", cancellationToken);
                    }

                    var text = await pageFetcher.FetchTextAsync(
                        hit.Url, options.PageExcerptChars, cancellationToken);
                    if (text is { Length: > 200 })
                    {
                        pagesRead++;
                        pageExcerpts.Add((hit.Url, text));
                    }
                }
            }
        }

        if (report is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return Stopped("synthesis returned unparseable output");
        }

        var verdict = NormalizeVerdict(report.Verdict);
        db.ResearchReports.Add(new ResearchReportEntity
        {
            IdeaId = idea.Id,
            Verdict = verdict,
            Confidence = Math.Clamp(report.Confidence, 0, 1),
            ReportJson = JsonSerializer.Serialize(report, LlmJson.Options),
            QueriesJson = JsonSerializer.Serialize(
                new { queries = blocks.Select(b => b.Query), rounds, pagesRead }, LlmJson.Options),
            SearchesUsed = searchesUsed,
            SourcesCount = blocks.Sum(b => b.Hits.Count),
            Model = options.Model,
            CostUsd = cost,
            CreatedAt = timeProvider.GetUtcNow(),
        });

        idea.Status = verdict switch
        {
            "go" => "hot",
            "no-go" => "dismissed",
            _ => "uncertain",
        };

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Research #{IdeaId}: {Verdict} (conf {Confidence:F2}), {Rounds} rounds, {Pages} pages, ${Cost:F4}",
            idea.Id, verdict, report.Confidence, rounds, pagesRead, cost);

        return new ResearchRunResult(
            FormatReport(idea, verdict, report, openQuestions, cost, searchesUsed, rounds, pagesRead),
            verdict, cost, searchesUsed, null);
    }

    private async Task<int> SearchIntoAsync(
        List<(string Query, IReadOnlyList<SearchHit> Hits)> blocks,
        IReadOnlyList<string> queries,
        long ideaId,
        IProgressHandle? progress,
        ResearchOptions options,
        CancellationToken cancellationToken)
    {
        var used = 0;
        foreach (var query in queries)
        {
            if (blocks.Any(b => b.Query.Equals(query, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (progress is not null)
            {
                await progress.UpdateAsync(
                    $"Research #{ideaId} · searching: {Truncate(query, 45)}…", cancellationToken);
            }

            await Task.Delay(options.SearchDelayMs, cancellationToken);
            blocks.Add((query, await brave.SearchAsync(query, options.ResultsPerQuery, cancellationToken)));
            used++;
        }

        return used;
    }

    private async Task<ResearchReportDto?> SynthesizeAsync(
        string ideaContext,
        List<(string Query, IReadOnlyList<SearchHit> Hits)> blocks,
        List<(string Url, string Excerpt)> pageExcerpts,
        ResearchOptions options,
        Action<decimal> addCost,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var completion = await chat.CompleteAsync(
                options.Model, ResearchPrompts.SynthesisSystem,
                ResearchPrompts.BuildSynthesisMessage(ideaContext, blocks, pageExcerpts),
                options.MaxCompletionTokens, options.ReasoningEffort, cancellationToken);
            addCost(RecordLedger(completion, options));

            var parsed = LlmJson.TryParse<ResearchReportDto>(completion?.Content);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    private static List<string> UnansweredQuestions(
        ResearchReportDto report, IReadOnlyList<string> openQuestions)
    {
        var answers = report.Answers ?? [];
        var unanswered = answers
            .Where(a => !a.IsAnswered && !string.IsNullOrWhiteSpace(a.Question))
            .Select(a => a.Question!)
            .ToList();

        // Questions the model silently dropped count as unanswered too.
        foreach (var question in openQuestions)
        {
            var covered = answers.Any(a =>
                a.Question is { } q && q.Contains(question[..Math.Min(30, question.Length)],
                    StringComparison.OrdinalIgnoreCase));
            if (!covered)
            {
                unanswered.Add(question);
            }
        }

        return unanswered.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
        IdeaEntity idea,
        string verdict,
        ResearchReportDto report,
        IReadOnlyList<string> openQuestions,
        decimal cost,
        int searches,
        int rounds,
        int pagesRead)
    {
        var builder = new StringBuilder();
        builder.Append(Ui.Research).Append(" <b>Research #").Append(idea.Id).Append(" · ")
            .Append(WebUtility.HtmlEncode(idea.Title)).Append("</b>\n")
            .Append("Verdict: <b>").Append(Ui.Verdict(verdict)).Append("</b> · confidence ")
            .Append((report.Confidence * 100).ToString("F0", CultureInfo.InvariantCulture))
            .Append("% → status ").Append(idea.Status).Append('\n');

        var competitors = (report.Competitors ?? []).Take(6).ToList();
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

        var answered = (report.Answers ?? []).Where(a => a.IsAnswered).Take(3).ToList();
        foreach (var answer in answered)
        {
            builder.Append('\n').Append("❓ ").Append(WebUtility.HtmlEncode(Truncate(answer.Question ?? string.Empty, 100)))
                .Append('\n').Append(Ui.Done).Append(' ')
                .Append(WebUtility.HtmlEncode(Truncate(answer.Answer ?? string.Empty, 220))).Append('\n');
        }

        var stillOpen = UnansweredQuestions(report, openQuestions).Take(3).ToList();
        if (stillOpen.Count > 0)
        {
            builder.Append("\n<b>🕳 Still open after ").Append(rounds).Append(" rounds</b>\n");
            foreach (var question in stillOpen)
            {
                builder.Append("• ").Append(WebUtility.HtmlEncode(Truncate(question, 110))).Append('\n');
            }
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

        var reportVariants = (report.RelatedVariants ?? []).Take(4).ToList();
        if (reportVariants.Count > 0)
        {
            builder.Append("<b>🔀 Stronger variants:</b> ")
                .Append(WebUtility.HtmlEncode(Truncate(string.Join(" · ", reportVariants), 260))).Append('\n');
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
            .Append(" · ").Append(searches).Append(" searches · ").Append(rounds).Append(" rounds · ")
            .Append(pagesRead).Append(" pages read · /idea ").Append(idea.Id);

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
}
