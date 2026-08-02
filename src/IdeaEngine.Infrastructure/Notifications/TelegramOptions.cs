namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>
/// Populated from environment variables (via .env): TELEGRAM_BOT_TOKEN, TELEGRAM_ADMIN_CHAT_ID.
/// </summary>
public sealed class TelegramOptions
{
    public string? BotToken { get; set; }

    public long? AdminChatId { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BotToken) && AdminChatId is not null and not 0;
}
