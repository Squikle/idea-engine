namespace IdeaEngine.Core.Notifications;

/// <summary>
/// A single always-visible status surface (pinned Telegram message in production).
/// Created and pinned once per process start, then edited in place as state changes.
/// </summary>
public interface IStatusBoard
{
    /// <summary>Send a fresh status message, unpin previous ones, pin the new one.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <param name="activity">Short state: "Collecting", "Idle", "Analyzing".</param>
    /// <param name="detail">Optional context: "FourChan: +24 new".</param>
    /// <param name="nextCycleAt">When the next ingestion cycle is scheduled, if known.</param>
    Task UpdateAsync(string activity, string? detail, DateTimeOffset? nextCycleAt, CancellationToken cancellationToken);

    /// <summary>Final edit on any exit path. Best-effort, never throws, self-timeboxed.</summary>
    Task OfflineAsync(string reason);
}
