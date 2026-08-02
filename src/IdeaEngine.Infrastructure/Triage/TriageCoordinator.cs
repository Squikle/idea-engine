using System.Globalization;
using System.Net;
using System.Text;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ingestion;
using IdeaEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Triage;

/// <summary>Aggregate outcome of draining the triage queue.</summary>
public sealed record TriageDrainResult(
    int Rounds, int Analyzed, int SignalsFound, decimal CostUsd, int Queued, bool Capped);

/// <summary>
/// Single entry point for triage processing - scheduled polls and the /analyze command
/// share it, so two drains can never double-process the same batch.
/// </summary>
public sealed class TriageCoordinator(
    IServiceScopeFactory scopeFactory,
    IStatusBoard statusBoard,
    IngestionCoordinator ingestion,
    INotifier notifier,
    TimeProvider timeProvider,
    IOptions<TriageOptions> triageOptions,
    ILogger<TriageCoordinator> logger) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateOnly _capNotifiedOn;

    public bool IsRunning => _lock.CurrentCount == 0;

    /// <returns>Aggregate result, or null when a drain was already in progress.</returns>
    public async Task<TriageDrainResult?> TryDrainAsync(CancellationToken cancellationToken)
    {
        if (!await _lock.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return null;
        }

        try
        {
            var drainStartedAt = timeProvider.GetUtcNow();
            var rounds = 0;
            var analyzed = 0;
            var signals = 0;
            decimal cost = 0;
            var queued = 0;
            var capped = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                using var scope = scopeFactory.CreateScope();
                var triage = scope.ServiceProvider.GetRequiredService<TriageService>();
                var round = await triage.RunRoundAsync(cancellationToken);

                rounds++;
                analyzed += round.Analyzed;
                signals += round.SignalsFound;
                cost += round.CostUsd;
                queued = round.Queued;
                capped = round.BudgetExhausted;

                if (capped)
                {
                    await NotifyCapOnceAsync(round.StopReason, cancellationToken);
                    break;
                }

                if (round.Analyzed == 0)
                {
                    break; // queue drained
                }

                if (!ingestion.IsRunning)
                {
                    await statusBoard.UpdateAsync(
                        "Analyzing",
                        $"{queued} queued · +{signals} signals",
                        ingestion.NextCycleAt,
                        cancellationToken);
                }
            }

            if (analyzed > 0 && !ingestion.IsRunning)
            {
                await statusBoard.UpdateAsync(
                    "Idle",
                    $"analyzed {analyzed} · +{signals} signals",
                    ingestion.NextCycleAt,
                    CancellationToken.None);
            }

            if (signals > 0 && triageOptions.Value.NotifyAfterDrain)
            {
                await NotifyDrainAsync(drainStartedAt, analyzed, signals, cost, cancellationToken);
            }

            return new TriageDrainResult(rounds, analyzed, signals, cost, queued, capped);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task NotifyDrainAsync(
        DateTimeOffset since, int analyzed, int signals, decimal cost, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();

            var fresh = await db.Signals
                .Where(s => s.CreatedAt >= since)
                .Select(s => new
                {
                    s.Kind,
                    s.Summary,
                    s.CommercialSentiment,
                    s.Confidence,
                    s.Novelty,
                    s.RawItem!.Url,
                    s.RawItem.Source,
                })
                .ToListAsync(cancellationToken);

            var top = fresh
                .Select(s => new
                {
                    s.Kind,
                    s.Summary,
                    s.Url,
                    s.Source,
                    Value = SignalScoring.Value(s.Confidence, s.Novelty, s.CommercialSentiment),
                })
                .OrderByDescending(s => s.Value)
                .Take(3)
                .ToList();

            var builder = new StringBuilder();
            builder.Append("<b>").Append(Ui.Analyze).Append(" Analyzed</b> ").Append(analyzed).Append(" items → +")
                .Append(signals).Append(" signals · $")
                .Append(cost.ToString("F3", CultureInfo.InvariantCulture)).Append('\n');

            var rank = 1;
            foreach (var signal in top)
            {
                builder.Append(rank++).Append(". v")
                    .Append(signal.Value.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(' ').Append(Ui.Kind(signal.Kind)).Append(' ')
                    .Append(WebUtility.HtmlEncode(
                        signal.Summary.Length > 90 ? signal.Summary[..89] + "…" : signal.Summary));

                if (signal.Url is { Length: > 0 })
                {
                    builder.Append(" <a href=\"").Append(signal.Url).Append("\">[")
                        .Append(signal.Source).Append("]</a>");
                }

                builder.Append('\n');
            }

            builder.Append("/best for the full ranking");
            await notifier.SendAsync(builder.ToString(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Drain notification failed (non-fatal)");
        }
    }

    private async Task NotifyCapOnceAsync(string? reason, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_capNotifiedOn == today)
        {
            return;
        }

        _capNotifiedOn = today;
        var detail = reason ?? $"daily cap ${triageOptions.Value.DailyUsdCap} reached";
        logger.LogWarning("Triage paused by budget guard: {Reason}", detail);
        await notifier.SendAsync($"Triage paused: {detail}. Resumes after midnight UTC.", cancellationToken);
    }

    public void Dispose() => _lock.Dispose();
}
