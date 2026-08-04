using System.Text.Json;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Triage;

/// <summary>Outcome of one triage round.</summary>
public sealed record TriageRoundResult(
    int Prefiltered, int Analyzed, int SignalsFound, decimal CostUsd,
    bool BudgetExhausted, int Queued, string? StopReason = null);

/// <summary>
/// One triage round: promote New items through the prefilter, then run a batch of
/// PendingTriage items through the analyzer. Every model call lands in ai_ledger;
/// the daily cap stops spending, never the process.
/// </summary>
public sealed class TriageService(
    IdeaEngineDbContext db,
    ITriageAnalyzer analyzer,
    BudgetGuard budgetGuard,
    TimeProvider timeProvider,
    IOptions<TriageOptions> triageOptions,
    IdeaEngine.Infrastructure.Ai.ModelRegistry models,
    ILogger<TriageService> logger)
{
    private const string StageName = "triage";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TriageRoundResult> RunRoundAsync(CancellationToken cancellationToken)
    {
        var __base = triageOptions.Value;
        var options = __base.WithModel(
            await models.ResolveAsync("triage", __base.Model,
                __base.InputPricePerMTok, __base.OutputPricePerMTok, cancellationToken));

        var prefiltered = await PromoteNewItemsAsync(cancellationToken);

        var queued = await db.RawItems.CountAsync(
            r => r.Status == ItemStatus.PendingTriage, cancellationToken);

        var worstCall = WorstCallUsd(options);
        var check = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, worstCall, worstCall * options.BatchSize, cancellationToken);
        if (!check.Allowed)
        {
            return new TriageRoundResult(
                prefiltered, 0, 0, 0, BudgetExhausted: true, queued, check.Reason);
        }

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        var batch = await db.RawItems
            .Where(r => r.Status == ItemStatus.PendingTriage)
            .OrderByDescending(r => r.Score)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            return new TriageRoundResult(prefiltered, 0, 0, 0, BudgetExhausted: false, Queued: 0);
        }

        var outcomes = new List<(RawItemEntity Item, TriageOutcome Outcome)>();
        await Parallel.ForEachAsync(
            batch,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.Parallelism,
                CancellationToken = cancellationToken,
            },
            async (item, ct) =>
            {
                var outcome = await analyzer.AnalyzeAsync(ToInput(item), ct);
                lock (outcomes)
                {
                    outcomes.Add((item, outcome));
                }
            });

        var now = timeProvider.GetUtcNow();
        var signalsFound = 0;
        decimal roundCost = 0;

        foreach (var (item, outcome) in outcomes)
        {
            var cost = ComputeCost(outcome, options);
            roundCost += cost;

            db.AiLedger.Add(new AiLedgerEntry
            {
                Day = today,
                Stage = StageName,
                Model = options.Model,
                TokensIn = outcome.TokensIn,
                TokensOut = outcome.TokensOut,
                CostUsd = cost,
                CreatedAt = now,
            });

            if (outcome.Verdict is not { } verdict)
            {
                item.Status = ItemStatus.Failed;
                continue;
            }

            item.Status = ItemStatus.Triaged;
            foreach (var draft in verdict.Signals)
            {
                signalsFound++;
                db.Signals.Add(new SignalEntity
                {
                    RawItemId = item.Id,
                    Kind = draft.Kind,
                    Summary = draft.Summary,
                    Audience = draft.Audience,
                    CommercialSentiment = draft.CommercialSentiment,
                    Novelty = draft.Novelty,
                    Confidence = draft.Confidence,
                    Model = options.Model,
                    CreatedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Triage round: {Analyzed} analyzed, {Signals} signals, ${Cost:F4}, {Queued} still queued",
            batch.Count, signalsFound, roundCost, Math.Max(0, queued - batch.Count));

        return new TriageRoundResult(
            prefiltered, batch.Count, signalsFound, roundCost,
            BudgetExhausted: false, Queued: Math.Max(0, queued - batch.Count));
    }

    private async Task<int> PromoteNewItemsAsync(CancellationToken cancellationToken)
    {
        var fresh = await db.RawItems
            .Where(r => r.Status == ItemStatus.New)
            .OrderByDescending(r => r.Score)
            .Take(200)
            .ToListAsync(cancellationToken);

        if (fresh.Count == 0)
        {
            return 0;
        }

        var filteredOut = 0;
        foreach (var item in fresh)
        {
            if (Prefilter.ShouldAnalyze(item, out var reason))
            {
                item.Status = ItemStatus.PendingTriage;
            }
            else
            {
                item.Status = ItemStatus.FilteredOut;
                filteredOut++;
                logger.LogDebug("Prefiltered out ({Reason}): {Title}", reason, item.Title);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return filteredOut;
    }

    private static TriageInput ToInput(RawItemEntity item)
    {
        IReadOnlyList<RawComment> comments = [];
        if (item.CommentsJson is { Length: > 0 } json)
        {
            try
            {
                comments = JsonSerializer.Deserialize<List<RawComment>>(json, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                // Corrupt comment payloads must not block triage of the item itself.
            }
        }

        return new TriageInput(
            item.Id, item.Source, item.Community, item.Title, item.Body,
            item.Score, item.CommentCount, comments);
    }

    private static decimal ComputeCost(TriageOutcome outcome, TriageOptions options) =>
        (outcome.TokensIn * options.InputPricePerMTok / 1_000_000m) +
        (outcome.TokensOut * options.OutputPricePerMTok / 1_000_000m);

    private static decimal WorstCallUsd(TriageOptions options) =>
        (4_000m * options.InputPricePerMTok
            + options.MaxCompletionTokens * options.OutputPricePerMTok) / 1_000_000m;
}
