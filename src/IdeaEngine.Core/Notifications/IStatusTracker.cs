namespace IdeaEngine.Core.Notifications;

/// <summary>Well-known process tracks shown on the status board.</summary>
public static class Tracks
{
    public const string Collect = "collect";
    public const string Analyze = "analyze";
    public const string Ideate = "ideate";
    public const string Research = "research";
    public const string DigestTrack = "digest";

    public static readonly IReadOnlyList<string> All =
        [Collect, Analyze, Ideate, Research, DigestTrack];
}

/// <summary>State of one process track.</summary>
public sealed record TrackState(
    bool Active,
    string? Detail,
    DateTimeOffset? StartedAt,
    string? LastResult,
    DateTimeOffset? LastFinishedAt,
    DateTimeOffset? NextRunAt);

/// <summary>Immutable snapshot of every track for rendering.</summary>
public sealed record StatusSnapshot(
    IReadOnlyDictionary<string, TrackState> TrackStates,
    DateTimeOffset WorkerStartedAt);

/// <summary>
/// Multi-track status surface: every pipeline process keeps its own always-visible line
/// (pinned Telegram message, edited in place). Concurrent processes never overwrite each
/// other. Implementations never throw.
/// </summary>
public interface IStatusTracker
{
    /// <summary>Send + pin a fresh board message (once per process start).</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Mark a track active with an initial detail.</summary>
    Task BeginAsync(string track, string? detail, CancellationToken cancellationToken);

    /// <summary>Update the live detail of an active track.</summary>
    Task UpdateAsync(string track, string detail, CancellationToken cancellationToken);

    /// <summary>Mark a track finished, recording the result shown while idle.</summary>
    Task EndAsync(string track, string? lastResult, CancellationToken cancellationToken);

    /// <summary>Record when the track is scheduled to run next (null = on demand/continuous).</summary>
    Task ScheduleAsync(string track, DateTimeOffset? nextRunAt, CancellationToken cancellationToken);

    /// <summary>Final edit on any exit path. Best-effort, never throws, self-timeboxed.</summary>
    Task OfflineAsync(string reason);

    StatusSnapshot Snapshot();
}
