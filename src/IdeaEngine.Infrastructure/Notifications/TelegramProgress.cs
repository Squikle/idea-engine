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
    public Task<IProgressHandle> StartAsync(string text, CancellationToken cancellationToken) =>
        StartAsync(text, null, cancellationToken);

    public async Task<IProgressHandle> StartAsync(
        string text, int? replyToMessageId, CancellationToken cancellationToken)
    {
        try
        {
            var message = await botClient.SendMessage(
                chatId: adminChatId,
                text: text,
                parseMode: ParseMode.Html,
                replyParameters: replyToMessageId is { } replyId
                    ? new Telegram.Bot.Types.ReplyParameters { MessageId = replyId }
                    : null,
                linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                cancellationToken: cancellationToken);

            return new TelegramProgressHandle(
                botClient, adminChatId, message.MessageId, text, timeProvider, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Progress message creation failed; continuing without progress");
            return NullProgressHandle.Instance;
        }
    }
}

internal sealed class TelegramProgressHandle : IProgressHandle
{
    private static readonly TimeSpan MinEditInterval = TimeSpan.FromSeconds(1.5);
    private const int MaxRenderedChars = 3600;

    private readonly ITelegramBotClient _botClient;
    private readonly long _chatId;
    private readonly int _messageId;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly List<string> _lines = [];

    private string? _lastSentText;
    private DateTimeOffset _lastEditAt;

    public int? MessageId => _messageId;

    public TelegramProgressHandle(
        ITelegramBotClient botClient,
        long chatId,
        int messageId,
        string headerText,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _botClient = botClient;
        _chatId = chatId;
        _messageId = messageId;
        _timeProvider = timeProvider;
        _logger = logger;
        _lines.Add(headerText);
    }

    /// <summary>Replaces the header line; used once the subject (idea title) is known.</summary>
    public Task SetHeaderAsync(string text, CancellationToken cancellationToken)
    {
        lock (_lines)
        {
            _lines[0] = text;
        }

        return EditAsync(force: true, cancellationToken);
    }

    /// <summary>Appends a step to the log (never replaces) - the message reads as history.</summary>
    public Task UpdateAsync(string text, CancellationToken cancellationToken)
    {
        AppendLine("• " + text);
        return EditAsync(force: false, cancellationToken);
    }

    public Task CompleteAsync(string text, CancellationToken cancellationToken)
    {
        AppendLine(text);
        return EditAsync(force: true, cancellationToken);
    }

    private void AppendLine(string line)
    {
        lock (_lines)
        {
            if (_lines.Count > 0 && _lines[^1] == line)
            {
                return;
            }

            _lines.Add(line);
        }
    }

    private string RenderLog()
    {
        lock (_lines)
        {
            var text = string.Join('\n', _lines);
            if (text.Length <= MaxRenderedChars)
            {
                return text;
            }

            // Keep the header and as many recent lines as fit.
            var kept = new List<string> { _lines[0], "<i>… earlier steps trimmed …</i>" };
            var budget = MaxRenderedChars - kept.Sum(l => l.Length + 1);
            var tail = new List<string>();
            for (var i = _lines.Count - 1; i > 0 && budget > 0; i--)
            {
                budget -= _lines[i].Length + 1;
                if (budget > 0)
                {
                    tail.Add(_lines[i]);
                }
            }

            tail.Reverse();
            kept.AddRange(tail);
            return string.Join('\n', kept);
        }
    }

    private async Task EditAsync(bool force, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (!force && now - _lastEditAt < MinEditInterval)
        {
            return; // buffered; next event (or Complete) flushes the accumulated log
        }

        var text = RenderLog();
        if (text == _lastSentText)
        {
            return;
        }

        try
        {
            await _botClient.EditMessageText(
                chatId: _chatId,
                messageId: _messageId,
                text: text,
                parseMode: ParseMode.Html,
                linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                cancellationToken: cancellationToken);
            _lastSentText = text;
            _lastEditAt = now;
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            _lastSentText = text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Progress edit failed (non-fatal)");
        }
    }
}

/// <summary>No-op when Telegram is not configured.</summary>
public sealed class NullProgressNotifier : IProgressNotifier
{
    public Task<IProgressHandle> StartAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult<IProgressHandle>(NullProgressHandle.Instance);

    public Task<IProgressHandle> StartAsync(string text, int? replyToMessageId, CancellationToken cancellationToken) =>
        Task.FromResult<IProgressHandle>(NullProgressHandle.Instance);
}

internal sealed class NullProgressHandle : IProgressHandle
{
    public static readonly NullProgressHandle Instance = new();

    public int? MessageId => null;

    public Task UpdateAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetHeaderAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
}
