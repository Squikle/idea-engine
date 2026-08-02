using System.Globalization;
using System.Net;
using System.Text;

namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>Renders the pinned status message (Telegram HTML). Pure and testable.</summary>
public static class StatusRenderer
{
    public static string RenderLive(
        string activity,
        string? detail,
        DateTimeOffset? nextCycleAt,
        DateTimeOffset startedAt,
        DateTimeOffset now)
    {
        var builder = new StringBuilder();
        builder.Append("<b>● idea-engine</b>\n");

        builder.Append(WebUtility.HtmlEncode(activity));
        if (!string.IsNullOrWhiteSpace(detail))
        {
            builder.Append(" — ").Append(WebUtility.HtmlEncode(detail));
        }

        builder.Append('\n');

        if (nextCycleAt is { } next)
        {
            var wait = next - now;
            builder.Append("Next cycle: ")
                .Append(next.ToString("HH:mm", CultureInfo.InvariantCulture))
                .Append(" UTC (in ")
                .Append(FormatDuration(wait))
                .Append(")\n");
        }

        builder.Append("Up since ")
            .Append(startedAt.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture))
            .Append(" UTC · upd ")
            .Append(now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

        return builder.ToString();
    }

    public static string RenderOffline(string reason, DateTimeOffset now) =>
        $"<b>○ idea-engine — OFFLINE</b>\n{WebUtility.HtmlEncode(reason)}\n{now.ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture)} UTC";

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}h {value.Minutes:D2}m"
            : $"{value.Minutes}m {value.Seconds:D2}s";
    }
}
