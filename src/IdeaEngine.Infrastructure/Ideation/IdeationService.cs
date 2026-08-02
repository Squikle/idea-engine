using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Ideation;

public sealed record IdeationBatchResult(
    int Sessions, int Advanced, int Killed, int Errors, decimal CostUsd,
    string? StoppedReason, IReadOnlyList<string> Lines);

public sealed record MetaAdviceResult(string Html, int ProposalsCount, decimal CostUsd, string? StoppedReason);

public sealed record OperatorIdeaResult(long? IdeaId, string Html, decimal CostUsd, string? StoppedReason);

/// <summary>
/// AI ideation sessions grounded in collected signals. Builder (strong model) proposes ONE
/// idea citing signal ids; Skeptic (different vendor) attacks it; verdict decides
/// candidate vs dismissed - both are stored. Meta mode asks for pipeline improvements
/// instead of product ideas. Every call passes the BudgetGuard first.
/// </summary>
public sealed class IdeationService(
    IdeaEngineDbContext db,
    OpenRouterChatClient chat,
    BudgetGuard budgetGuard,
    IStatusBoard statusBoard,
    IAdviceJournal adviceJournal,
    TimeProvider timeProvider,
    IOptions<IdeationOptions> ideationOptions,
    ILogger<IdeationService> logger)
{
    private const string StageName = "ideation";

    public async Task<IdeationBatchResult> RunProductSessionsAsync(
        int count, IProgressHandle? progress, CancellationToken cancellationToken)
    {
        var options = ideationOptions.Value;
        count = Math.Clamp(count, 1, options.MaxSessionsPerCommand);

        if (!options.Enabled || !chat.IsConfigured)
        {
            return new IdeationBatchResult(0, 0, 0, 0, 0, "ideation disabled or OPENROUTER_API_KEY missing", []);
        }

        var pool = await LoadSignalPoolAsync(options, cancellationToken);
        if (pool.Count < 8)
        {
            return new IdeationBatchResult(
                0, 0, 0, 0, 0, $"not enough signals yet ({pool.Count}; need 8+) - let triage run first", []);
        }

        var advanced = 0;
        var killed = 0;
        var errors = 0;
        decimal totalCost = 0;
        var lines = new List<string>();
        string? stoppedReason = null;

        for (var session = 1; session <= count; session++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var check = await budgetGuard.CheckAsync(
                StageName, options.DailyUsdCap, WorstBuilderCallUsd(options),
                WorstSessionUsd(options), cancellationToken);
            if (!check.Allowed)
            {
                stoppedReason = check.Reason;
                break;
            }

            await statusBoard.UpdateAsync(
                "Ideating", $"session {session}/{count}", null, cancellationToken);
            if (progress is not null)
            {
                await progress.UpdateAsync(
                    $"Ideation {session}/{count} · {advanced} live · {killed} killed · builder thinking…",
                    cancellationToken);
            }

            var outcome = await RunSingleSessionAsync(pool, options, progress, session, count, cancellationToken);
            totalCost += outcome.Cost;

            switch (outcome.Kind)
            {
                case SessionOutcomeKind.Advanced:
                    advanced++;
                    lines.Add(outcome.Line!);
                    break;
                case SessionOutcomeKind.Killed:
                    killed++;
                    lines.Add(outcome.Line!);
                    break;
                default:
                    errors++;
                    break;
            }
        }

        logger.LogInformation(
            "Ideation batch: {Advanced} advanced, {Killed} killed, {Errors} errors, ${Cost:F4}",
            advanced, killed, errors, totalCost);

        return new IdeationBatchResult(
            advanced + killed + errors, advanced, killed, errors, totalCost, stoppedReason, lines);
    }

    /// <summary>
    /// /drop: shape the operator's raw pitch into a structured idea, run the skeptic,
    /// store with Origin=operator. The command layer chains web research afterwards.
    /// </summary>
    public async Task<OperatorIdeaResult> RunOperatorIdeaAsync(
        string pitch, IProgressHandle? progress, CancellationToken cancellationToken)
    {
        var options = ideationOptions.Value;
        if (!options.Enabled || !chat.IsConfigured)
        {
            return new OperatorIdeaResult(null, string.Empty, 0, "ideation disabled or OPENROUTER_API_KEY missing");
        }

        var check = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, WorstBuilderCallUsd(options),
            WorstSessionUsd(options), cancellationToken);
        if (!check.Allowed)
        {
            return new OperatorIdeaResult(null, string.Empty, 0, check.Reason);
        }

        await statusBoard.UpdateAsync("Ideating", "shaping your idea", null, cancellationToken);
        if (progress is not null)
        {
            await progress.UpdateAsync("Drop · shaping your pitch into a structured idea…", cancellationToken);
        }

        decimal cost = 0;
        var shaping = await chat.CompleteAsync(
            options.BuilderModel, IdeationPrompts.OperatorIdeaSystem, pitch,
            options.MaxCompletionTokens, options.ReasoningEffort, cancellationToken);
        cost += RecordLedger(options.BuilderModel, shaping,
            options.BuilderInputPricePerMTok, options.BuilderOutputPricePerMTok);

        var idea = LlmJson.TryParse<BuilderIdeaDto>(shaping?.Content);
        if (idea is null || string.IsNullOrWhiteSpace(idea.Title))
        {
            await db.SaveChangesAsync(cancellationToken);
            return new OperatorIdeaResult(null, string.Empty, cost, "could not shape the pitch (model output unparseable)");
        }

        if (progress is not null)
        {
            await progress.UpdateAsync(
                $"Drop · skeptic attacking \"{Truncate(idea.Title, 45)}\"…", cancellationToken);
        }

        SkepticReview? review = null;
        var skepticCompletion = await chat.CompleteAsync(
            options.SkepticModel, IdeationPrompts.SkepticSystem,
            IdeationPrompts.BuildSkepticMessage(shaping!.Content!, []) +
            "\nNote: operator-submitted idea. Judge on merits; verdict stays honest.",
            options.MaxCompletionTokens, options.ReasoningEffort, cancellationToken);
        cost += RecordLedger(options.SkepticModel, skepticCompletion,
            options.SkepticInputPricePerMTok, options.SkepticOutputPricePerMTok);
        review = LlmJson.TryParse<SkepticReview>(skepticCompletion?.Content);

        var advanced = string.Equals(review?.Verdict, "advance", StringComparison.OrdinalIgnoreCase);
        var entity = new IdeaEntity
        {
            Title = Truncate(idea.Title, 290)!,
            Thesis = idea.Thesis ?? pitch,
            Category = NormalizeCategory(idea.Category),
            EffortScale = Math.Clamp(idea.Effort, 1, 5),
            TargetUser = Truncate(idea.TargetUser, 290),
            Monetization = Truncate(idea.Monetization, 590),
            DistributionNote = Truncate(idea.DistributionNote, 390),
            Status = advanced ? "candidate" : "dismissed",
            Origin = "operator",
            VariantsJson = idea.Variants is { Count: > 0 }
                ? JsonSerializer.Serialize(idea.Variants.Take(6), LlmJson.Options)
                : null,
            ScoresJson = review?.Scores is null ? null : JsonSerializer.Serialize(review.Scores, LlmJson.Options),
            SkepticJson = review is null ? null : JsonSerializer.Serialize(review, LlmJson.Options),
            BuilderModel = options.BuilderModel,
            SkepticModel = options.SkepticModel,
            CostUsd = cost,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.Ideas.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        var builder = new StringBuilder();
        builder.Append("<b>#").Append(entity.Id).Append(" · ")
            .Append(WebUtility.HtmlEncode(entity.Title)).Append("</b> (yours)\n")
            .Append("Skeptic: ").Append(advanced ? "advance" : "kill");
        if (review?.KillReasons is { Count: > 0 } reasons && !advanced)
        {
            builder.Append(" — ").Append(WebUtility.HtmlEncode(Truncate(reasons[0], 120)!));
        }

        builder.Append('\n');
        if (idea.Variants is { Count: > 0 })
        {
            builder.Append("Variants: ")
                .Append(WebUtility.HtmlEncode(Truncate(string.Join(" · ", idea.Variants.Take(5)), 300)!))
                .Append('\n');
        }

        return new OperatorIdeaResult(entity.Id, builder.ToString().TrimEnd(), cost, null);
    }

    public async Task<MetaAdviceResult> RunMetaSessionAsync(
        IProgressHandle? progress, CancellationToken cancellationToken)
    {
        var options = ideationOptions.Value;
        if (!options.Enabled || !chat.IsConfigured)
        {
            return new MetaAdviceResult(string.Empty, 0, 0, "ideation disabled or OPENROUTER_API_KEY missing");
        }

        var check = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, WorstBuilderCallUsd(options),
            WorstBuilderCallUsd(options), cancellationToken);
        if (!check.Allowed)
        {
            return new MetaAdviceResult(string.Empty, 0, 0, check.Reason);
        }

        await statusBoard.UpdateAsync("Advising", "pipeline self-review", null, cancellationToken);
        if (progress is not null)
        {
            await progress.UpdateAsync("Advisor · gathering pipeline stats…", cancellationToken);
        }

        var statsMessage = await BuildPipelineStatsAsync(cancellationToken);

        if (progress is not null)
        {
            await progress.UpdateAsync(
                $"Advisor · {options.BuilderModel} reviewing the pipeline…", cancellationToken);
        }

        var completion = await chat.CompleteAsync(
            options.BuilderModel, IdeationPrompts.MetaSystem, statsMessage,
            options.MaxCompletionTokens, options.ReasoningEffort, cancellationToken);

        var cost = RecordLedger(options.BuilderModel, completion,
            options.BuilderInputPricePerMTok, options.BuilderOutputPricePerMTok);

        var advice = LlmJson.TryParse<MetaAdviceDto>(completion?.Content);
        if (advice?.Proposals is not { Count: > 0 } proposals)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new MetaAdviceResult(string.Empty, 0, cost, "advisor returned nothing parseable");
        }

        var now = timeProvider.GetUtcNow();
        var builder = new StringBuilder("<b>Pipeline advice</b>\n");
        var journal = new StringBuilder();
        journal.Append("## ").Append(now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Append(" UTC · advisor · ").Append(options.BuilderModel).Append('\n');

        var stored = 0;
        foreach (var proposal in proposals.Take(6))
        {
            if (string.IsNullOrWhiteSpace(proposal.Title))
            {
                continue;
            }

            stored++;
            db.Ideas.Add(new IdeaEntity
            {
                Title = Truncate(proposal.Title, 290)!,
                Thesis = $"{proposal.What}\nWhy: {proposal.Why}",
                Category = "meta",
                EffortScale = Math.Clamp(proposal.Effort, 1, 5),
                Status = "candidate",
                SkepticJson = JsonSerializer.Serialize(new { proposal.Verify }, LlmJson.Options),
                BuilderModel = options.BuilderModel,
                CostUsd = 0,
                CreatedAt = now,
            });

            builder.Append("• <b>").Append(WebUtility.HtmlEncode(proposal.Title)).Append("</b> [")
                .Append(proposal.Kind ?? "other").Append(", e").Append(Math.Clamp(proposal.Effort, 1, 5))
                .Append("]\n").Append(WebUtility.HtmlEncode(proposal.What ?? string.Empty))
                .Append("\n<i>verify: ").Append(WebUtility.HtmlEncode(proposal.Verify ?? "-"))
                .Append("</i>\n");

            journal.Append("- **").Append(proposal.Title).Append("** [")
                .Append(proposal.Kind ?? "other").Append(", e").Append(Math.Clamp(proposal.Effort, 1, 5))
                .Append("] — ").Append(proposal.What)
                .Append(" Why: ").Append(proposal.Why)
                .Append(" Verify: ").Append(proposal.Verify).Append('\n');
        }

        builder.Append("\ncost: $").Append(cost.ToString("F4", CultureInfo.InvariantCulture));
        await db.SaveChangesAsync(cancellationToken);
        await adviceJournal.AppendAsync(journal.ToString(), cancellationToken);

        return new MetaAdviceResult(builder.ToString(), stored, cost, null);
    }

    private async Task<SessionOutcome> RunSingleSessionAsync(
        IReadOnlyList<GroundingSignal> pool,
        IdeationOptions options,
        IProgressHandle? progress,
        int session,
        int count,
        CancellationToken cancellationToken)
    {
        decimal cost = 0;

        // Vary grounding per session so ten sessions explore, not repeat.
        var sample = SampleSignals(pool, options.SignalsPerSession);
        var builderCompletion = await chat.CompleteAsync(
            options.BuilderModel, IdeationPrompts.BuilderSystem, IdeationPrompts.BuildGrounding(sample),
            options.MaxCompletionTokens, options.ReasoningEffort, cancellationToken);
        cost += RecordLedger(options.BuilderModel, builderCompletion,
            options.BuilderInputPricePerMTok, options.BuilderOutputPricePerMTok);

        var idea = LlmJson.TryParse<BuilderIdeaDto>(builderCompletion?.Content);
        if (idea is null || string.IsNullOrWhiteSpace(idea.Title))
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Builder produced unparseable idea (finish={Finish})", builderCompletion?.FinishReason);
            return new SessionOutcome(SessionOutcomeKind.Error, null, cost);
        }

        var citedIds = ParseCitedIds(idea.CitedSignals);
        var citedSignals = pool.Where(s => citedIds.Contains(s.Id)).ToList();

        if (progress is not null)
        {
            await progress.UpdateAsync(
                $"Ideation {session}/{count} · skeptic attacking \"{Truncate(idea.Title, 45)}\"…",
                cancellationToken);
        }

        var skepticCheck = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, WorstSkepticCallUsd(options),
            WorstSkepticCallUsd(options), cancellationToken);

        SkepticReview? review = null;
        if (skepticCheck.Allowed)
        {
            var skepticCompletion = await chat.CompleteAsync(
                options.SkepticModel, IdeationPrompts.SkepticSystem,
                IdeationPrompts.BuildSkepticMessage(builderCompletion!.Content!, citedSignals),
                options.MaxCompletionTokens, options.ReasoningEffort, cancellationToken);
            cost += RecordLedger(options.SkepticModel, skepticCompletion,
                options.SkepticInputPricePerMTok, options.SkepticOutputPricePerMTok);
            review = LlmJson.TryParse<SkepticReview>(skepticCompletion?.Content);
        }

        // No skeptic verdict -> no free pass. Unvetted ideas are dismissed with a reason.
        var advanced = string.Equals(review?.Verdict, "advance", StringComparison.OrdinalIgnoreCase);
        var killReason = review is null
            ? "skeptic unavailable - never advance unvetted"
            : review.KillReasons is { Count: > 0 } reasons
                ? reasons[0]
                : null;

        var entity = new IdeaEntity
        {
            Title = Truncate(idea.Title, 290)!,
            Thesis = idea.Thesis ?? string.Empty,
            Category = NormalizeCategory(idea.Category),
            EffortScale = Math.Clamp(idea.Effort, 1, 5),
            TargetUser = Truncate(idea.TargetUser, 290),
            Monetization = Truncate(idea.Monetization, 590),
            DistributionNote = Truncate(idea.DistributionNote, 390),
            Status = advanced ? "candidate" : "dismissed",
            EvidenceJson = JsonSerializer.Serialize(citedIds, LlmJson.Options),
            ScoresJson = review?.Scores is null ? null : JsonSerializer.Serialize(review.Scores, LlmJson.Options),
            SkepticJson = review is null ? null : JsonSerializer.Serialize(review, LlmJson.Options),
            BuilderModel = options.BuilderModel,
            SkepticModel = options.SkepticModel,
            CostUsd = cost,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.Ideas.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        var line = advanced
            ? $"LIVE [{entity.Category}/e{entity.EffortScale}] {entity.Title}"
            : $"killed [{entity.Category}/e{entity.EffortScale}] {entity.Title} — {Truncate(killReason, 90) ?? "skeptic said no"}";

        return new SessionOutcome(
            advanced ? SessionOutcomeKind.Advanced : SessionOutcomeKind.Killed, line, cost);
    }

    private async Task<List<GroundingSignal>> LoadSignalPoolAsync(
        IdeationOptions options, CancellationToken cancellationToken)
    {
        return await db.Signals
            .Where(s => s.Confidence >= options.MinSignalConfidence
                && s.CommercialSentiment != "no_market")
            .OrderByDescending(s => s.Confidence + s.Novelty)
            .Take(options.SignalPoolSize)
            .Select(s => new GroundingSignal(
                s.Id, s.Kind, s.CommercialSentiment, s.Confidence,
                s.Summary, s.Audience, s.RawItem!.Community))
            .ToListAsync(cancellationToken);
    }

    private async Task<string> BuildPipelineStatsAsync(CancellationToken cancellationToken)
    {
        var bySource = await db.RawItems
            .GroupBy(r => r.Source)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var signalCount = await db.Signals.CountAsync(cancellationToken);
        var topCommunities = await db.Signals
            .Where(s => s.RawItem!.Community != null)
            .GroupBy(s => s.RawItem!.Community)
            .OrderByDescending(g => g.Count())
            .Take(8)
            .Select(g => g.Key + ":" + g.Count())
            .ToListAsync(cancellationToken);

        var builder = new StringBuilder(
            """
            Pipeline: C# worker collects public posts, cheap-LLM triage extracts product-opportunity
            signals, ideation sessions synthesize ideas (this system).
            Live sources: HackerNews (Algolia API), Reddit via RSS only (Data API approval pending;
            position=score proxy, no comments), 4chan (diy,g boards), Lemmy (lemmy.world), Bluesky
            (blocked: search needs auth, app password pending).
            Planned: YouTube trending+comments, GDELT news, Product Hunt, Etsy/eBay, AliExpress
            affiliate hot products, Google Trends (gated alpha).
            Parked for ToS: TikTok, Instagram, FB Marketplace, X (paywalled).
            Policies: official APIs/feeds only, no AI training on Reddit data, no user profiling.
            Operator: solo dev (C#), 3D printing access, Ukraine+Canada distribution focus.

            Live stats:
            """);

        foreach (var row in bySource)
        {
            builder.Append(row.Source).Append('=').Append(row.Count).Append(' ');
        }

        builder.Append("| signals=").Append(signalCount)
            .Append(" | top signal communities: ").AppendJoin(", ", topCommunities);

        return builder.ToString();
    }

    private decimal RecordLedger(
        string model, ChatCompletion? completion, decimal inPricePerMTok, decimal outPricePerMTok)
    {
        if (completion is null)
        {
            return 0;
        }

        var cost = (completion.TokensIn * inPricePerMTok / 1_000_000m)
            + (completion.TokensOut * outPricePerMTok / 1_000_000m);

        db.AiLedger.Add(new AiLedgerEntry
        {
            Day = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime),
            Stage = StageName,
            Model = model,
            TokensIn = completion.TokensIn,
            TokensOut = completion.TokensOut,
            CostUsd = cost,
            CreatedAt = timeProvider.GetUtcNow(),
        });

        return cost;
    }

    private static List<GroundingSignal> SampleSignals(IReadOnlyList<GroundingSignal> pool, int count)
    {
        if (pool.Count <= count)
        {
            return [.. pool];
        }

        var indices = Enumerable.Range(0, pool.Count).ToArray();
        Random.Shared.Shuffle(indices);
        return [.. indices.Take(count).Select(i => pool[i]).OrderByDescending(s => s.Confidence)];
    }

    private static HashSet<long> ParseCitedIds(IReadOnlyList<string>? cited) =>
        (cited ?? [])
            .Select(c => long.TryParse(c.TrimStart('S', 's'), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();

    private static string NormalizeCategory(string? category)
    {
        var value = category?.Trim().ToLowerInvariant();
        return value is "saas" or "app" or "website" or "3dprint" or "hardware" or "wearable" or "service" or "content"
            ? value
            : "other";
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private static decimal WorstBuilderCallUsd(IdeationOptions o) =>
        (8_000m * o.BuilderInputPricePerMTok + o.MaxCompletionTokens * o.BuilderOutputPricePerMTok) / 1_000_000m;

    private static decimal WorstSkepticCallUsd(IdeationOptions o) =>
        (8_000m * o.SkepticInputPricePerMTok + o.MaxCompletionTokens * o.SkepticOutputPricePerMTok) / 1_000_000m;

    private static decimal WorstSessionUsd(IdeationOptions o) =>
        WorstBuilderCallUsd(o) + WorstSkepticCallUsd(o);

    private enum SessionOutcomeKind
    {
        Advanced,
        Killed,
        Error,
    }

    private sealed record SessionOutcome(SessionOutcomeKind Kind, string? Line, decimal Cost);

    private sealed record BuilderIdeaDto(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("thesis")] string? Thesis,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("target_user")] string? TargetUser,
        [property: JsonPropertyName("effort")] int Effort,
        [property: JsonPropertyName("monetization")] string? Monetization,
        [property: JsonPropertyName("distribution_note")] string? DistributionNote,
        [property: JsonPropertyName("cited_signals")] IReadOnlyList<string>? CitedSignals,
        [property: JsonPropertyName("variants")] IReadOnlyList<string>? Variants,
        [property: JsonPropertyName("assumptions")] IReadOnlyList<string>? Assumptions);


    private sealed record MetaAdviceDto(
        [property: JsonPropertyName("proposals")] IReadOnlyList<MetaProposalDto>? Proposals);

    private sealed record MetaProposalDto(
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("what")] string? What,
        [property: JsonPropertyName("why")] string? Why,
        [property: JsonPropertyName("verify")] string? Verify,
        [property: JsonPropertyName("effort")] int Effort);
}
