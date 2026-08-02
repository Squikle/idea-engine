namespace IdeaEngine.Worker;

/// <summary>Logs the startup banner and a quiet periodic heartbeat. Owner-facing state lives on the status board.</summary>
internal sealed class StartupSummaryService(
    IHostEnvironment environment,
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
