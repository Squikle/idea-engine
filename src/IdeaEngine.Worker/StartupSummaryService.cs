using IdeaEngine.Core.Notifications;

namespace IdeaEngine.Worker;

/// <summary>Logs a startup banner, pings the owner via Telegram, then heartbeats quietly.</summary>
internal sealed class StartupSummaryService(
    IHostEnvironment environment,
    INotifier notifier,
    ILogger<StartupSummaryService> logger) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var version = typeof(StartupSummaryService).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        logger.LogInformation(
            "idea-engine {Version} started ({Environment})",
            version,
            environment.EnvironmentName);

        await notifier.SendAsync(
            $"Worker online ({environment.EnvironmentName}, v{version}). First collection starts shortly.",
            stoppingToken);

        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                logger.LogDebug("Heartbeat: worker alive, pipeline idle");
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
    }
}
