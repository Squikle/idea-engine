namespace IdeaEngine.Core.Notifications;

/// <summary>
/// Outbound owner-facing notifications (Telegram in production).
/// Message text is Telegram-flavored HTML; implementations that cannot render HTML
/// must strip tags, not reject.
/// </summary>
public interface INotifier
{
    Task SendAsync(string html, CancellationToken cancellationToken);
}
