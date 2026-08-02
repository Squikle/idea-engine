using IdeaEngine.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace IdeaEngine.Infrastructure.Research;

/// <summary>
/// Single-flight gate for research runs, shared by /research, the /drop chain and the
/// autopilot - one web-research report at a time, no double-processing.
/// </summary>
public sealed class ResearchCoordinator(IServiceScopeFactory scopeFactory) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _gate.CurrentCount == 0;

    /// <param name="wait">true: queue behind the current run; false: return null when busy.</param>
    public async Task<ResearchRunResult?> RunAsync(
        long ideaId, IProgressHandle? progress, bool wait, CancellationToken cancellationToken)
    {
        if (wait)
        {
            await _gate.WaitAsync(cancellationToken);
        }
        else if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return null;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var research = scope.ServiceProvider.GetRequiredService<ResearchService>();
            return await research.RunAsync(ideaId, progress, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
