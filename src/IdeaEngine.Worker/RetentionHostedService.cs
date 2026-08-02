using IdeaEngine.Infrastructure.Maintenance;

namespace IdeaEngine.Worker;

/// <summary>Runs retention/compliance once shortly after startup, then daily.</summary>
internal sealed class RetentionHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<RetentionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        await RunSafeAsync(stoppingToken);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunSafeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
    }

    private async Task RunSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Retention run failed; next attempt in 24h");
        }
    }
}
