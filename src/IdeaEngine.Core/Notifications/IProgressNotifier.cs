namespace IdeaEngine.Core.Notifications;

/// <summary>
/// One live progress surface per long-running operation: a single message edited in
/// place through the steps (never a message per step). Implementations never throw.
/// </summary>
public interface IProgressNotifier
{
    Task<IProgressHandle> StartAsync(string text, CancellationToken cancellationToken);

    /// <summary>Start a progress log as a REPLY to an existing message (tap-through from ack).</summary>
    Task<IProgressHandle> StartAsync(string text, int? replyToMessageId, CancellationToken cancellationToken);
}

/// <summary>Handle to the operation's progress message.</summary>
public interface IProgressHandle
{
    /// <summary>Telegram message id of the log, when one exists.</summary>
    int? MessageId { get; }

    /// <summary>Edit the message in place (throttled; identical text skipped).</summary>
    Task UpdateAsync(string text, CancellationToken cancellationToken);

    /// <summary>Replace the header line (e.g. once the idea title becomes known).</summary>
    Task SetHeaderAsync(string text, CancellationToken cancellationToken);

    /// <summary>Final edit; always applied regardless of throttling.</summary>
    Task CompleteAsync(string text, CancellationToken cancellationToken);
}
