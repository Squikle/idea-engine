using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using IdeaEngine.Core.Notifications;
using Serilog.Core;
using Serilog.Events;

namespace IdeaEngine.Worker;

/// <summary>
/// Warning+ log events become Telegram alerts, deduplicated per fingerprint (1/hour) so a
/// flapping source cannot spam. Notification-layer categories are excluded to avoid loops.
/// The notifier is attached after DI is up; events before that are dropped by design.
/// </summary>
internal sealed class TelegramLogSink : ILogEventSink
{
    public static readonly TelegramLogSink Instance = new();

    private static readonly TimeSpan DedupWindow = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    private volatile INotifier? _notifier;

    public void Attach(INotifier notifier) => _notifier = notifier;

    public void Emit(LogEvent logEvent)
    {
        if (_notifier is not { } notifier || logEvent.Level < LogEventLevel.Warning)
        {
            return;
        }

        var sourceContext = logEvent.Properties.TryGetValue("SourceContext", out var value)
            ? value.ToString().Trim('"')
            : string.Empty;

        // Never alert about the alerting/notification path itself.
        if (sourceContext.Contains("Notifications", StringComparison.Ordinal)
            || sourceContext.Contains("Telegram", StringComparison.Ordinal))
        {
            return;
        }

        var fingerprint =
            $"{logEvent.Level}|{sourceContext}|{logEvent.Exception?.GetType().Name ?? logEvent.MessageTemplate.Text}";
        var now = DateTimeOffset.UtcNow;
        if (_lastSent.TryGetValue(fingerprint, out var last) && now - last < DedupWindow)
        {
            return;
        }

        _lastSent[fingerprint] = now;

        var shortContext = sourceContext[(sourceContext.LastIndexOf('.') + 1)..];
        var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        var icon = logEvent.Level >= LogEventLevel.Error ? "🔴" : "🟠";
        var text = $"{icon} <b>{logEvent.Level}</b> · {WebUtility.HtmlEncode(shortContext)}\n" +
            WebUtility.HtmlEncode(Truncate(message, 300));
        if (logEvent.Exception is { } exception)
        {
            text += $"\n<i>{WebUtility.HtmlEncode(exception.GetType().Name)}: " +
                $"{WebUtility.HtmlEncode(Truncate(exception.Message, 200))}</i>";
        }

        _ = Task.Run(() => notifier.SendAsync(text, CancellationToken.None));
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
