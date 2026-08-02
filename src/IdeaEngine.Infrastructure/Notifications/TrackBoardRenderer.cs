using System.Globalization;
using System.Text;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Notifications;

namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>Renders the multi-track pinned board (Telegram HTML). Pure and testable.</summary>
public static class TrackBoardRenderer
{
    public static string Render(StatusSnapshot snapshot, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var label = Scheduling.ZoneLabel(timeZone);
        var builder = new StringBuilder();

        builder.Append("<b>").Append(Ui.Live).Append(" idea-engine</b> · up ")
            .Append(FormatDuration(now - snapshot.WorkerStartedAt)).Append('\n');

        foreach (var track in Tracks.All)
        {
            if (!snapshot.TrackStates.TryGetValue(track, out var state))
            {
                state = new TrackState(false, null, null, null, null, null);
            }

            builder.Append(TrackIcon(track)).Append(' ').Append(track).Append(": ");

            if (state.Active)
            {
                builder.Append("⏳ ").Append(Escape(state.Detail ?? "working…"));
            }
            else
            {
                var parts = new List<string>();
                if (state.LastResult is { Length: > 0 } result && state.LastFinishedAt is { } finished)
                {
                    parts.Add($"last {Local(finished, timeZone)} · {result}");
                }
                else if (state.LastResult is { Length: > 0 } bare)
                {
                    parts.Add(bare);
                }

                if (state.NextRunAt is { } next && next > now)
                {
                    parts.Add($"next {Local(next, timeZone)}");
                }

                builder.Append(parts.Count > 0 ? Escape(string.Join(" · ", parts)) : "—");
            }

            builder.Append('\n');
        }

        builder.Append("<i>upd ")
            .Append(TimeZoneInfo.ConvertTime(now, timeZone).ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            .Append(' ').Append(label).Append("</i>");

        return builder.ToString();
    }

    public static string RenderOffline(string reason, DateTimeOffset now, TimeZoneInfo timeZone) =>
        $"<b>{Ui.Offline} idea-engine — OFFLINE</b>\n{Escape(reason)}\n" +
        $"{TimeZoneInfo.ConvertTime(now, timeZone).ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture)} " +
        Scheduling.ZoneLabel(timeZone);

    /// <summary>Minimal escape: keeps emojis/middots readable, blocks HTML injection.</summary>
    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string TrackIcon(string track) => track switch
    {
        Tracks.Collect => Ui.Collect,
        Tracks.Analyze => Ui.Analyze,
        Tracks.Ideate => Ui.Ideate,
        Tracks.Research => Ui.Research,
        Tracks.DigestTrack => Ui.Digest,
        _ => Ui.Live,
    };

    private static string Local(DateTimeOffset at, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(at, timeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalDays >= 1
            ? $"{(int)value.TotalDays}d {value.Hours}h"
            : value.TotalHours >= 1
                ? $"{(int)value.TotalHours}h {value.Minutes:D2}m"
                : $"{value.Minutes}m";
    }
}
