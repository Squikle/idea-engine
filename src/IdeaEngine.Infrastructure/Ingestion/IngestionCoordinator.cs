using IdeaEngine.Core.Notifications;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Core.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdeaEngine.Infrastructure.Ingestion;

/// <summary>
/// Single entry point for running ingestion cycles - scheduled or manual (bot command).
/// Guarantees one cycle at a time and owns the Collecting/Idle status transitions.
/// </summary>
public sealed class IngestionCoordinator(
    IServiceScopeFactory scopeFactory,
    IStatusTracker statusTracker,
    ILogger<IngestionCoordinator> logger) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Next scheduled cycle, maintained by the hosted scheduler.</summary>
    public DateTimeOffset? NextCycleAt { get; set; }

    public IngestionCycleReport? LastReport { get; private set; }

    public bool IsRunning => _lock.CurrentCount == 0;

    /// <returns>The cycle report, or null when a cycle was already running.</returns>
    public async Task<IngestionCycleReport?> TryRunAsync(SourceKind? only, CancellationToken cancellationToken)
    {
        if (!await _lock.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            logger.LogInformation("Ingestion trigger ignored: a cycle is already running");
            return null;
        }

        try
        {
            var label = only is { } kind ? $"{kind} (manual)…" : "all sources…";
            await statusTracker.BeginAsync(Tracks.Collect, label, cancellationToken);

            using var scope = scopeFactory.CreateScope();
            var ingestion = scope.ServiceProvider.GetRequiredService<IngestionService>();
            var report = await ingestion.RunAsync(only, cancellationToken);
            LastReport = report;
            return report;
        }
        finally
        {
            var result = LastReport is { } last ? $"+{last.TotalStored} items" : null;
            await statusTracker.EndAsync(Tracks.Collect, result, CancellationToken.None);
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}
