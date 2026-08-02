using System.Globalization;
using System.Net;
using System.Text;
using IdeaEngine.Core.Common;

namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>Renders the pinned status message (Telegram HTML) in the owner's time zone.</summary>
public static class StatusRenderer
{
    public static string RenderLive(
        string activity,
        string? detail,
        DateTimeOffset? nextCycleAt,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        var label = Scheduling.ZoneLabel(timeZone);
        var builder = new StringBuilder();
        builder.Append("<b>").Append(Ui.Live).Append(" idea-engine</b>\n");

        builder.Append(Ui.Activity(activity)).Append(' ').Append(WebUtility.HtmlEncode(activity));
        if (!string.IsNullOrWhiteSpace(detail))
        {
            builder.Append(" — ").Append(WebUtility.HtmlEncode(detail));
        }

        builder.Append('\n');

        if (nextCycleAt is { } next)
        {
            var wait = next - now;
            builder.Append("Next cycle: ")
                .Append(TimeZoneInfo.ConvertTime(next, timeZone).ToString("HH:mm", CultureInfo.InvariantCulture))
                .Append(' ').Append(label)
                .Append(" (in ")
                .Append(FormatDuration(wait))
                .Append(")\n");
        }

        builder.Append("Up since ")
            .Append(TimeZoneInfo.ConvertTime(startedAt, timeZone).ToString("dd MMM HH:mm", CultureInfo.InvariantCulture))
            .Append(' ').Append(label).Append(" · upd ")
            .Append(TimeZoneInfo.ConvertTime(now, timeZone).ToString("HH:mm:ss", CultureInfo.InvariantCulture));

        return builder.ToString();
    }

    public static string RenderOffline(string reason, DateTimeOffset now, TimeZoneInfo timeZone) =>
        $"<b>{Ui.Offline} idea-engine — OFFLINE</b>\n{WebUtility.HtmlEncode(reason)}\n" +
        $"{TimeZoneInfo.ConvertTime(now, timeZone).ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture)} " +
        Scheduling.ZoneLabel(timeZone);

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
