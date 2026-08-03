using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ideation;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Research;

public sealed record ResearchRunResult(
    string Html, string? Verdict, decimal CostUsd, int SearchesUsed, string? StoppedReason,
    long IdeaId = 0, double Score = 0, double Confidence = 0);

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

        var previousReport = await db.ResearchReports
            .Where(r => r.IdeaId == idea.Id)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (previousReport is not null)
        {
            var previousDto = LlmJson.SafeDeserialize<ResearchReportDto>(previousReport.ReportJson);
            var stillOpen = previousDto is null ? [] : UnansweredQuestions(previousDto, []);
            var missedUpgrades = ReasoningMilestones.MissedSince(previousReport.EngineVersion);

            ideaContext += $"\nPREVIOUS RESEARCH (engine v{previousReport.EngineVersion ?? "pre-0.22"}, " +
                $"verdict {previousReport.Verdict}, confidence {previousReport.Confidence:F2}): build ON TOP of it - " +
                "do NOT redo settled findings; verify only what could have changed and close the gaps below.\n";
            if (stillOpen.Count > 0)
            {
                ideaContext += "Still open from last time:\n"
                    + string.Join('\n', stillOpen.Take(5).Select(q => "- " + q)) + "\n";
            }

            if (missedUpgrades.Count > 0)
            {
                ideaContext += "Reasoning upgrades since that verdict (apply them now):\n"
                    + string.Join('\n', missedUpgrades.Select(m => "- " + m.Summary)) + "\n";
            }
        }

        var notes = SafeDeserialize<List<IdeaNote>>(idea.NotesJson);
        if (notes is { Count: > 0 })
        {
            ideaContext += "Operator's notes/counter-arguments (address each explicitly):\n"
                + string.Join('\n', notes.TakeLast(5).Select(n => "- " + n.Text)) + "\n";
        }

        // The deepening loop: research → appeal → note → research. Each pass must
        // digest the court's critique, or re-running research would change nothing.
        if (idea.AppealJson is { Length: > 0 })
        {
            ideaContext += "\nCOURT OF APPEAL review of the previous verdict (a stronger model "
                + "audited the judgment; explicitly address its 'missed' points this round):\n"
                + idea.AppealJson + "\n";
        }

        decimal cost = 0;
        var searchesUsed = 0;
        var pagesRead = 0;
        var blocks = new List<(string Query, IReadOnlyList<SearchHit> Hits)>();
        var pageExcerpts = new List<(string Url, string Excerpt)>();

        // Round 1 queries: planned by the model from the idea + open questions.
        if (progress is not null)
        {
            await progress.UpdateAsync("1/4 planning web queries…", cancellationToken);
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

        var artifacts = new List<ResearchArtifactEntity>();

        // Debate: advocate builds the case FOR before the judge synthesizes.
        string? advocateJson = null;
        if (progress is not null)
        {
            await progress.UpdateAsync("3/4 ⚖️ advocate building the case for…", cancellationToken);
        }

        var advocateCompletion = await chat.CompleteAsync(
            options.Model, ResearchPrompts.AdvocateSystem,
            ResearchPrompts.BuildSynthesisMessage(ideaContext, blocks),
            2500, "low", cancellationToken);
        cost += RecordLedger(advocateCompletion, options);
        if (advocateCompletion?.Content is { Length: > 50 } advocateContent
            && advocateContent.Contains("case_for", StringComparison.OrdinalIgnoreCase))
        {
            advocateJson = advocateContent;
        }

        if (advocateJson is not null)
        {
            ideaContext += "\nADVOCATE'S CASE (weigh honestly against the skeptic and evidence):\n"
                + advocateJson + "\n";
            AddArtifact(artifacts, idea.Id, "advocate", 0, new { raw = advocateJson });
        }

        // Synthesis rounds until closure or MaxRounds.
        ResearchReportDto? report = null;
        string? lastSynthesisDiag = null;
        string? lastSynthesisRaw = null;
        var rounds = 0;
        while (rounds < Math.Max(1, options.MaxRounds))
        {
            rounds++;
            if (progress is not null)
            {
                await progress.UpdateAsync(
                    $"4/4 round {rounds}: judging {blocks.Sum(b => b.Hits.Count)} snippets + {pageExcerpts.Count} pages…",
                    cancellationToken);
            }

            string? synthesisDiag;
            string? synthesisRaw;
            (report, synthesisDiag, synthesisRaw) = await SynthesizeAsync(
                ideaContext, blocks, pageExcerpts, options, c => cost += c, cancellationToken);
            if (report is null)
            {
                lastSynthesisDiag = synthesisDiag;
                lastSynthesisRaw = synthesisRaw;
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
                    $"4/4 digging into {unanswered.Count} open questions…", cancellationToken);
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
                            $"reading {(Uri.TryCreate(hit.Url, UriKind.Absolute, out var pageUri) ? pageUri.Host : "page")}…",
                            cancellationToken);
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

        PersistScaffolding(artifacts, idea.Id, blocks, pageExcerpts);
        if (report is null)
        {
            // Failed runs keep their scaffolding too (ReportId null): the searches are
            // paid for and the raw reply is the debugging evidence.
            if (lastSynthesisRaw is { Length: > 0 })
            {
                AddArtifact(artifacts, idea.Id, "synthesis_raw", 0, new { raw = lastSynthesisRaw });
            }

            db.ResearchArtifacts.AddRange(artifacts);
            await db.SaveChangesAsync(cancellationToken);
            return Stopped($"synthesis failed — {lastSynthesisDiag ?? "unknown model failure"}");
        }

        var verdict = NormalizeVerdict(report.Verdict);
        var reportEntity = new ResearchReportEntity
        {
            IdeaId = idea.Id,
            Verdict = verdict,
            Confidence = Math.Clamp(report.Confidence, 0, 1),
            ReportJson = JsonSerializer.Serialize(report, LlmJson.Options),
            QueriesJson = JsonSerializer.Serialize(
                new { queries = blocks.Select(b => b.Query), rounds, pagesRead }, LlmJson.Options),
            SearchesUsed = searchesUsed,
            EngineVersion = typeof(ResearchService).Assembly.GetName().Version?.ToString(3),
            SourcesCount = blocks.Sum(b => b.Hits.Count),
            Model = options.Model,
            CostUsd = cost,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.ResearchReports.Add(reportEntity);

        idea.Status = verdict switch
        {
            "go" => "hot",
            "no-go" => "dismissed",
            _ => "uncertain",
        };

        await db.SaveChangesAsync(cancellationToken);

        // Scaffolding gets the report id only after the report row exists.
        foreach (var artifact in artifacts)
        {
            artifact.ReportId = reportEntity.Id;
        }

        db.ResearchArtifacts.AddRange(artifacts);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Research #{IdeaId}: {Verdict} (conf {Confidence:F2}), {Rounds} rounds, {Pages} pages, ${Cost:F4}",
            idea.Id, verdict, report.Confidence, rounds, pagesRead, cost);

        var unified = IdeaScoring.Compute(
            SafeDeserialize<Dictionary<string, double>>(idea.ScoresJson),
            skeptic?.Confidence ?? 0,
            report.Scores,
            report.Confidence);

        return new ResearchRunResult(
            FormatReport(idea, verdict, report, openQuestions, cost, searchesUsed, rounds, pagesRead, unified),
            verdict, cost, searchesUsed, null, idea.Id, unified.Total, Math.Clamp(report.Confidence, 0, 1));
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
                    $"2/4 searching: {Truncate(query, 45)}…", cancellationToken);
            }

            await Task.Delay(options.SearchDelayMs, cancellationToken);
            blocks.Add((query, await brave.SearchAsync(query, options.ResultsPerQuery, cancellationToken)));
            used++;
        }

        return used;
    }

    private async Task<(ResearchReportDto? Report, string? Diag, string? Raw)> SynthesizeAsync(
        string ideaContext,
        List<(string Query, IReadOnlyList<SearchHit> Hits)> blocks,
        List<(string Url, string Excerpt)> pageExcerpts,
        ResearchOptions options,
        Action<decimal> addCost,
        CancellationToken cancellationToken)
    {
        ChatCompletion? last = null;
        var tokenBudget = options.MaxCompletionTokens;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            last = await chat.CompleteAsync(
                options.Model, ResearchPrompts.SynthesisSystem,
                ResearchPrompts.BuildSynthesisMessage(ideaContext, blocks, pageExcerpts),
                tokenBudget, options.ReasoningEffort, cancellationToken);
            addCost(RecordLedger(last, options));

            var parsed = LlmJson.TryParse<ResearchReportDto>(last?.Content);
            if (parsed is not null)
            {
                return (parsed, null, last?.Content);
            }

            if (LlmDiag.IsTruncation(last))
            {
                tokenBudget = (int)(tokenBudget * 1.5); // retry with headroom
            }
        }

        // Deterministic repairs failed twice: one cheap re-emit before giving up.
        // A $0.30 research run must not die for a $0.003 syntax error.
        if (last?.Content is { Length: > 300 } broken)
        {
            var repair = await chat.CompleteAsync(
                options.RepairModel,
                "You fix malformed JSON. Output ONLY the corrected JSON object - identical "
                + "content, valid syntax. Never add, remove, translate or summarize anything.",
                broken, (int)(broken.Length / 2.5), "low", cancellationToken);
            if (repair is { IsError: false })
            {
                var repairCost = (repair.TokensIn * options.RepairInputPricePerMTok
                    + repair.TokensOut * options.RepairOutputPricePerMTok) / 1_000_000m;
                db.AiLedger.Add(new AiLedgerEntry
                {
                    Day = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime),
                    Stage = StageName,
                    Model = options.RepairModel,
                    TokensIn = repair.TokensIn,
                    TokensOut = repair.TokensOut,
                    CostUsd = repairCost,
                    CreatedAt = timeProvider.GetUtcNow(),
                });
                addCost(repairCost);

                var repaired = LlmJson.TryParse<ResearchReportDto>(repair.Content);
                if (repaired is not null)
                {
                    logger.LogInformation("Synthesis JSON repaired by {Model}", options.RepairModel);
                    return (repaired, null, last?.Content);
                }
            }
        }

        return (null, LlmDiag.Describe(last), last?.Content);
    }

    /// <summary>SERPs and page excerpts → artifacts (retro mining; costs nothing vs AI spend).</summary>
    private void PersistScaffolding(
        List<ResearchArtifactEntity> artifacts,
        long ideaId,
        List<(string Query, IReadOnlyList<SearchHit> Hits)> blocks,
        List<(string Url, string Excerpt)> pageExcerpts)
    {
        var seq = 0;
        foreach (var (query, hits) in blocks)
        {
            AddArtifact(artifacts, ideaId, "serp", seq++, new
            {
                query,
                hits = hits.Select(h => new { title = h.Title, url = h.Url, snippet = h.Description }),
            });
        }

        seq = 0;
        foreach (var (url, excerpt) in pageExcerpts)
        {
            AddArtifact(artifacts, ideaId, "page", seq++, new { url, excerpt });
        }
    }

    private void AddArtifact(
        List<ResearchArtifactEntity> artifacts, long ideaId, string kind, int seq, object payload)
    {
        artifacts.Add(new ResearchArtifactEntity
        {
            IdeaId = ideaId,
            Kind = kind,
            Seq = seq,
            Json = JsonSerializer.Serialize(payload, LlmJson.Options),
            CreatedAt = timeProvider.GetUtcNow(),
        });
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
        if (completion is null || completion.IsError)
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
        int pagesRead,
        IdeaScore unified)
    {
        var builder = new StringBuilder();
        builder.Append(Ui.Research).Append(" <b>Research #").Append(idea.Id).Append(" · ")
            .Append(WebUtility.HtmlEncode(idea.Title)).Append("</b>\n")
            .Append("Verdict: <b>").Append(Ui.Verdict(verdict)).Append("</b> → status ").Append(idea.Status).Append('\n')
            .Append("⭐ <b>Score ").Append((unified.Total * 100).ToString("F0", CultureInfo.InvariantCulture))
            .Append("%</b> <i>(opportunity strength)</i> · evidence ")
            .Append((report.Confidence * 100).ToString("F0", CultureInfo.InvariantCulture))
            .Append("% <i>(research solidity)</i>\n");

        var competitors = (report.Competitors ?? []).Take(8).ToList();
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

                builder.Append(" — ").Append(WebUtility.HtmlEncode(competitor.Why ?? string.Empty))
                    .Append('\n');
            }
        }

        var answered = (report.Answers ?? []).Where(a => a.IsAnswered).Take(8).ToList();
        if (answered.Count > 0)
        {
            builder.Append("\n<b>💬 Answered</b>\n");
        }

        foreach (var answer in answered)
        {
            builder.Append("🔹 <i>").Append(WebUtility.HtmlEncode(answer.Question ?? string.Empty))
                .Append("</i>\n↳ ").Append(Linkify.Render(answer.Answer, 600));
            var source = (answer.EvidenceUrls ?? []).FirstOrDefault(u => u is { Length: > 0 });
            if (source is not null && Uri.TryCreate(source, UriKind.Absolute, out var sourceUri))
            {
                builder.Append(" <a href=\"").Append(source).Append("\">[").Append(sourceUri.Host).Append("]</a>");
            }

            builder.Append('\n');
        }

        var stillOpen = UnansweredQuestions(report, openQuestions).Take(6).ToList();
        if (stillOpen.Count > 0)
        {
            builder.Append("\n<b>🕳 Still open after ").Append(rounds).Append(" rounds</b>\n");
            foreach (var question in stillOpen)
            {
                builder.Append("• ").Append(WebUtility.HtmlEncode(question)).Append('\n');
            }
        }

        if (!string.IsNullOrWhiteSpace(report.DifferentiationPath))
        {
            builder.Append("\n<b>🧭 Differentiation:</b> ")
                .Append(Linkify.Render(report.DifferentiationPath, 700)).Append('\n');
        }

        var risks = (report.Risks ?? []).Take(6).ToList();
        if (risks.Count > 0)
        {
            builder.Append("<b>⚠️ Risks</b>\n");
            foreach (var risk in risks)
            {
                builder.Append("• ").Append(WebUtility.HtmlEncode(risk)).Append('\n');
            }
        }

        if (!string.IsNullOrWhiteSpace(report.MvpTest))
        {
            builder.Append("<b>🧪 MVP test:</b> ")
                .Append(Linkify.Render(report.MvpTest, 600)).Append('\n');
        }

        var reportVariants = (report.RelatedVariants ?? []).Take(6).ToList();
        if (reportVariants.Count > 0)
        {
            builder.Append("<b>🔀 Stronger variants:</b> ")
                .Append(WebUtility.HtmlEncode(string.Join(" · ", reportVariants))).Append('\n');
        }

        var steps = (report.NextSteps ?? []).Take(6).ToList();
        if (steps.Count > 0)
        {
            builder.Append("<b>➡️ Next steps</b>\n");
            foreach (var step in steps)
            {
                builder.Append("→ ").Append(WebUtility.HtmlEncode(step)).Append('\n');
            }
        }

        builder.Append('\n').Append(Ui.Spend).Append(" $").Append(cost.ToString("F4", CultureInfo.InvariantCulture))
            .Append(" · ").Append(searches).Append(" searches · ").Append(rounds).Append(" rounds · ")
            .Append(pagesRead).Append(" pages read · /idea ").Append(idea.Id);

        // No amputation: TelegramNotifier splits oversized cards into reply-chained
        // messages at line boundaries. Arguments arrive whole or not at all.
        return builder.ToString().TrimEnd();
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

    internal sealed record IdeaNote(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("at")] DateTimeOffset At);
}
