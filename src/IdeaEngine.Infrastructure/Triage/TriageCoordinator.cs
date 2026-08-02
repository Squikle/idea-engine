using IdeaEngine.Core.Notifications;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ingestion;
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

            return new TriageDrainResult(rounds, analyzed, signals, cost, queued, capped);
        }
        finally
        {
            _lock.Release();
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
