using IdeaEngine.Infrastructure.Ingestion;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Worker;

/// <summary>
/// Drives ingestion cycles: once shortly after startup (if enabled), then on a fixed
/// interval. A crash inside a cycle is logged and the schedule continues; a missed
/// interval (host asleep) simply runs at the next tick.
/// </summary>
internal sealed class IngestionHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    ILogger<IngestionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;

        if (config.RunOnStartup)
        {
            // Small delay so startup logs settle and the host is fully up.
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            await RunOnceSafeAsync(stoppingToken);
        }

        var interval = TimeSpan.FromHours(config.IntervalHours);
        logger.LogInformation("Next ingestion cycles every {Hours:F1}h", interval.TotalHours);

        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceSafeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
    }

    private async Task RunOnceSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<IngestionService>();
            await ingestion.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ingestion cycle crashed; will retry at next interval");
        }
    }
}
