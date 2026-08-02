using IdeaEngine.Core.Notifications;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>Sends owner notifications to the admin chat. Failures are logged, never thrown.</summary>
public sealed class TelegramNotifier(
    ITelegramBotClient botClient,
    long adminChatId,
    ILogger<TelegramNotifier> logger) : INotifier
{
    public async Task SendAsync(string html, CancellationToken cancellationToken)
    {
        try
        {
            await botClient.SendMessage(
                chatId: adminChatId,
                text: html,
                parseMode: ParseMode.Html,
                linkPreviewOptions: LinkPreviewOptions.Disabled, // keeps messages compact
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Telegram send failed; message dropped ({Length} chars)", html.Length);
        }
    }
}
