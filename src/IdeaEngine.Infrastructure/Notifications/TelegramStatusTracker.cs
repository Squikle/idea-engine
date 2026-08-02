using System.Collections.Concurrent;
using IdeaEngine.Core.Notifications;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>
/// Pinned multi-track status board. One message per process start: sent, pinned
/// (previous pins cleared - the chat is dedicated to this bot), then edited on every
/// track change (throttled; Begin/End force an edit). Never throws.
/// </summary>
public sealed class TelegramStatusTracker(
    ITelegramBotClient botClient,
    long adminChatId,
    TimeProvider timeProvider,
    TimeZoneInfo timeZone,
    ILogger<TelegramStatusTracker> logger) : IStatusTracker, IDisposable
{
    private static readonly TimeSpan MinEditInterval = TimeSpan.FromSeconds(1.5);

    private readonly ConcurrentDictionary<string, TrackState> _tracks = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int? _messageId;
    private string? _lastText;
    private DateTimeOffset _lastEditAt;
    private DateTimeOffset _startedAt;

    public void Dispose() => _gate.Dispose();

    public StatusSnapshot Snapshot() =>
        new(new Dictionary<string, TrackState>(_tracks), _startedAt);

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
            var text = TrackBoardRenderer.Render(Snapshot(), _startedAt, timeZone);
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

    public Task BeginAsync(string track, string? detail, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        _tracks.AddOrUpdate(
            track,
            _ => new TrackState(true, detail, now, null, null, null),
            (_, prev) => prev with { Active = true, Detail = detail, StartedAt = now });
        return RenderAndEditAsync(force: true, cancellationToken);
    }

    public Task UpdateAsync(string track, string detail, CancellationToken cancellationToken)
    {
        _tracks.AddOrUpdate(
            track,
            _ => new TrackState(true, detail, timeProvider.GetUtcNow(), null, null, null),
            (_, prev) => prev with { Active = true, Detail = detail });
        return RenderAndEditAsync(force: false, cancellationToken);
    }

    public Task EndAsync(string track, string? lastResult, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        _tracks.AddOrUpdate(
            track,
            _ => new TrackState(false, null, null, lastResult, now, null),
            (_, prev) => prev with
            {
                Active = false,
                Detail = null,
                LastResult = lastResult ?? prev.LastResult,
                LastFinishedAt = now,
            });
        return RenderAndEditAsync(force: true, cancellationToken);
    }

    public Task ScheduleAsync(string track, DateTimeOffset? nextRunAt, CancellationToken cancellationToken)
    {
        _tracks.AddOrUpdate(
            track,
            _ => new TrackState(false, null, null, null, null, nextRunAt),
            (_, prev) => prev with { NextRunAt = nextRunAt });
        return RenderAndEditAsync(force: false, cancellationToken);
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
                text: TrackBoardRenderer.RenderOffline(reason, timeProvider.GetUtcNow(), timeZone),
                parseMode: ParseMode.Html,
                cancellationToken: timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Offline status edit failed");
        }
    }

    private async Task RenderAndEditAsync(bool force, CancellationToken cancellationToken)
    {
        if (_messageId is not { } messageId)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (!force && now - _lastEditAt < MinEditInterval)
        {
            return;
        }

        var text = TrackBoardRenderer.Render(Snapshot(), now, timeZone);
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
            _lastEditAt = now;
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            _lastText = text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Status board edit failed (non-fatal)");
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Stands in when Telegram is not configured.</summary>
public sealed class NullStatusTracker : IStatusTracker
{
    private static readonly StatusSnapshot Empty =
        new(new Dictionary<string, TrackState>(), DateTimeOffset.MinValue);

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task BeginAsync(string track, string? detail, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpdateAsync(string track, string detail, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EndAsync(string track, string? lastResult, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ScheduleAsync(string track, DateTimeOffset? nextRunAt, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OfflineAsync(string reason) => Task.CompletedTask;

    public StatusSnapshot Snapshot() => Empty;
}
