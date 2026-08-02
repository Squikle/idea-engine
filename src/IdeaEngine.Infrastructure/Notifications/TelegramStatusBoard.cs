using IdeaEngine.Core.Notifications;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>
/// Pinned-message status board. One message per process start: sent, pinned
/// (previous pins cleared - the bot chat is dedicated to this bot), then edited in place.
/// All operations are best-effort: status must never take the pipeline down.
/// </summary>
public sealed class TelegramStatusBoard(
    ITelegramBotClient botClient,
    long adminChatId,
    TimeProvider timeProvider,
    TimeZoneInfo timeZone,
    ILogger<TelegramStatusBoard> logger) : IStatusBoard, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();
    private int? _messageId;
    private string? _lastText;
    private DateTimeOffset _startedAt;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _startedAt = timeProvider.GetUtcNow();

        try
        {
            await botClient.UnpinAllChatMessages(adminChatId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Unpinning previous status messages failed (non-fatal)");
        }

        try
        {
            var text = StatusRenderer.RenderLive("Starting", null, null, _startedAt, _startedAt, timeZone);
            var message = await botClient.SendMessage(
                chatId: adminChatId,
                text: text,
                parseMode: ParseMode.Html,
                linkPreviewOptions: LinkPreviewOptions.Disabled,
                cancellationToken: cancellationToken);

            _messageId = message.MessageId;
            _lastText = text;

            await botClient.PinChatMessage(
                chatId: adminChatId,
                messageId: message.MessageId,
                disableNotification: true,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Status board initialization failed; continuing without pinned status");
        }
    }

    public async Task UpdateAsync(
        string activity, string? detail, DateTimeOffset? nextCycleAt, CancellationToken cancellationToken)
    {
        if (_messageId is not { } messageId)
        {
            return;
        }

        var text = StatusRenderer.RenderLive(
            activity, detail, nextCycleAt, _startedAt, timeProvider.GetUtcNow(), timeZone);

        if (text == _lastText)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await botClient.EditMessageText(
                chatId: adminChatId,
                messageId: messageId,
                text: text,
                parseMode: ParseMode.Html,
                linkPreviewOptions: LinkPreviewOptions.Disabled,
                cancellationToken: cancellationToken);
            _lastText = text;
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            _lastText = text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Status edit failed (non-fatal)");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OfflineAsync(string reason)
    {
        if (_messageId is not { } messageId)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await botClient.EditMessageText(
                chatId: adminChatId,
                messageId: messageId,
                text: StatusRenderer.RenderOffline(reason, timeProvider.GetUtcNow(), timeZone),
                parseMode: ParseMode.Html,
                cancellationToken: timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Offline status edit failed");
        }
    }
}
