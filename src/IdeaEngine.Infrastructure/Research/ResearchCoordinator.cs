using IdeaEngine.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace IdeaEngine.Infrastructure.Research;

/// <summary>
/// Single-flight gate for research runs, shared by /research, the /drop chain and the
/// autopilot - one web-research report at a time, no double-processing.
/// </summary>
public sealed class ResearchCoordinator(
    IServiceScopeFactory scopeFactory,
    IStatusTracker statusTracker) : IDisposable
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
            await statusTracker.BeginAsync(Tracks.Research, $"#{ideaId} planning…", cancellationToken);
            using var scope = scopeFactory.CreateScope();
            var research = scope.ServiceProvider.GetRequiredService<ResearchService>();
            var result = await research.RunAsync(ideaId, progress, cancellationToken);
            await statusTracker.EndAsync(
                Tracks.Research,
                result.StoppedReason is { } reason
                    ? $"#{ideaId} ⛔ {Truncate(reason, 40)}"
                    : $"#{ideaId} {Core.Common.Ui.Verdict(result.Verdict)}",
                CancellationToken.None);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    public void Dispose() => _gate.Dispose();
}
