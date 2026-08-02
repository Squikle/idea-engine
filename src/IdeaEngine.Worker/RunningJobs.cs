using System.Collections.Concurrent;

namespace IdeaEngine.Worker;

/// <summary>
/// Registry of in-flight jobs so /cancel can reach a RUNNING job's cancellation token.
/// Cancel semantics: stops at the next await; tokens already spent are lost; job → canceled.
/// </summary>
internal static class RunningJobs
{
    private static readonly ConcurrentDictionary<long, CancellationTokenSource> Active = new();

    public static void Register(long jobId, CancellationTokenSource manualCts) =>
        Active[jobId] = manualCts;

    public static void Unregister(long jobId) => Active.TryRemove(jobId, out _);

    public static bool TryCancel(long jobId)
    {
        if (!Active.TryGetValue(jobId, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
