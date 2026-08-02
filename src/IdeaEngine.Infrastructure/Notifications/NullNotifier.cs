using IdeaEngine.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>Stands in when Telegram is not configured; warns once, then stays silent.</summary>
public sealed class NullNotifier(ILogger<NullNotifier> logger) : INotifier
{
    private int _warned;

    public Task SendAsync(string html, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "Telegram not configured (TELEGRAM_BOT_TOKEN / TELEGRAM_ADMIN_CHAT_ID); notifications are dropped");
        }

        return Task.CompletedTask;
    }
}
