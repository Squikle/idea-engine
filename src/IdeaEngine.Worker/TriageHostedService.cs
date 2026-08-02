using IdeaEngine.Core.Pipeline;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Triage;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Worker;

/// <summary>
/// Polls the triage queue: drains it fully when there is work (via the coordinator,
/// shared with /analyze), waits when idle, backs off for 30 minutes on budget cap.
/// </summary>
internal sealed class TriageHostedService(
    TriageCoordinator coordinator,
    ITriageAnalyzer analyzer,
    IOptions<TriageOptions> options,
    ILogger<TriageHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        if (!config.Enabled)
        {
            logger.LogInformation("Triage disabled by configuration");
            return;
        }

        if (!analyzer.IsConfigured)
        {
            logger.LogWarning("Triage disabled: OPENROUTER_API_KEY is not set");
            return;
        }

        // Let ingestion's startup cycle get a head start.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        logger.LogInformation("Triage online: model {Model}, cap ${Cap}/day", config.Model, config.DailyUsdCap);

        while (!stoppingToken.IsCancellationRequested)
        {
            TriageDrainResult? result = null;
            try
            {
                result = await coordinator.TryDrainAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Triage drain crashed; retrying after idle delay");
            }

            var delay = result switch
            {
                { Capped: true } => TimeSpan.FromMinutes(30),
                null => TimeSpan.FromSeconds(30), // manual drain in progress
                _ => TimeSpan.FromSeconds(config.PollSecondsIdle),
            };

            await Task.Delay(delay, stoppingToken);
        }
    }
}
