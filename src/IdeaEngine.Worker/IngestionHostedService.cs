using IdeaEngine.Infrastructure.Ingestion;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Worker;

/// <summary>
/// Schedules ingestion cycles: once shortly after startup (if enabled), then on a fixed
/// interval. Actual execution goes through <see cref="IngestionCoordinator"/> so manual
/// bot triggers and the schedule can never overlap. A missed interval (host asleep)
/// simply runs at the next tick.
/// </summary>
internal sealed class IngestionHostedService(
    IngestionCoordinator coordinator,
    IdeaEngine.Core.Notifications.IStatusTracker statusTracker,
    TimeProvider timeProvider,
    IOptions<IngestionOptions> options,
    ILogger<IngestionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        var interval = TimeSpan.FromHours(config.IntervalHours);

        if (config.RunOnStartup)
        {
            // Small delay so the status board is initialized first.
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            await RunScheduledAsync(interval, stoppingToken);
        }

        logger.LogInformation("Ingestion scheduled every {Hours:F1}h", interval.TotalHours);

        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunScheduledAsync(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
    }

    private async Task RunScheduledAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        coordinator.NextCycleAt = timeProvider.GetUtcNow() + interval;
        await statusTracker.ScheduleAsync(
            IdeaEngine.Core.Notifications.Tracks.Collect, coordinator.NextCycleAt, cancellationToken);

        try
        {
            await coordinator.TryRunAsync(only: null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled ingestion cycle crashed; next attempt at {Next}", coordinator.NextCycleAt);
        }
    }
}
