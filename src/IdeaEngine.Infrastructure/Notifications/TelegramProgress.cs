using IdeaEngine.Core.Notifications;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>Creates one editable progress message per operation.</summary>
public sealed class TelegramProgressNotifier(
    ITelegramBotClient botClient,
    long adminChatId,
    TimeProvider timeProvider,
    ILogger<TelegramProgressNotifier> logger) : IProgressNotifier
{
    public async Task<IProgressHandle> StartAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            var message = await botClient.SendMessage(
                chatId: adminChatId,
                text: text,
                parseMode: ParseMode.Html,
                linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                cancellationToken: cancellationToken);

            return new TelegramProgressHandle(botClient, adminChatId, message.MessageId, timeProvider, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Progress message creation failed; continuing without progress");
            return NullProgressHandle.Instance;
        }
    }
}

internal sealed class TelegramProgressHandle(
    ITelegramBotClient botClient,
    long chatId,
    int messageId,
    TimeProvider timeProvider,
    ILogger logger) : IProgressHandle
{
    private static readonly TimeSpan MinEditInterval = TimeSpan.FromSeconds(1.5);

    private string? _lastText;
    private DateTimeOffset _lastEditAt;

    public Task UpdateAsync(string text, CancellationToken cancellationToken) =>
        EditAsync(text, force: false, cancellationToken);

    public Task CompleteAsync(string text, CancellationToken cancellationToken) =>
        EditAsync(text, force: true, cancellationToken);

    private async Task EditAsync(string text, bool force, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!force && (text == _lastText || now - _lastEditAt < MinEditInterval))
        {
            return;
        }

        try
        {
            await botClient.EditMessageText(
                chatId: chatId,
                messageId: messageId,
                text: text,
                parseMode: ParseMode.Html,
                linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                cancellationToken: cancellationToken);
            _lastText = text;
            _lastEditAt = now;
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            _lastText = text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Progress edit failed (non-fatal)");
        }
    }
}

/// <summary>No-op when Telegram is not configured.</summary>
public sealed class NullProgressNotifier : IProgressNotifier
{
    public Task<IProgressHandle> StartAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult<IProgressHandle>(NullProgressHandle.Instance);
}

internal sealed class NullProgressHandle : IProgressHandle
{
    public static readonly NullProgressHandle Instance = new();

    public Task UpdateAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
}
