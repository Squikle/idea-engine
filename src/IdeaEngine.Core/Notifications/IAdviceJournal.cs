namespace IdeaEngine.Core.Notifications;

/// <summary>
/// Append-only journal of AI-generated recommendations about the pipeline itself
/// (journal/advice.md). The operator's architect reads it during dev sessions and
/// decides what to apply; the app only ever appends.
/// </summary>
public interface IAdviceJournal
{
    Task AppendAsync(string markdown, CancellationToken cancellationToken);
}
