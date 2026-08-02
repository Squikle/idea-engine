using System.Globalization;
using System.Net;
using System.Text;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Pipeline;

namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>Formats a cycle report as a compact Telegram HTML message.</summary>
public static class IngestionReportFormatter
{
    private const int MaxHighlights = 5;
    private const int MaxTitleLength = 90;

    public static string Format(IngestionCycleReport report, TimeZoneInfo timeZone)
    {
        var builder = new StringBuilder();

        builder.Append("<b>").Append(Ui.Collect).Append(" Collected · ")
            .Append(TimeZoneInfo.ConvertTime(report.FinishedAt, timeZone)
                .ToString("dd MMM HH:mm", CultureInfo.InvariantCulture))
            .Append(' ').Append(Scheduling.ZoneLabel(timeZone)).Append("</b>\n");

        foreach (var source in report.Sources)
        {
            builder.Append(source.Source).Append(": ");

            if (source.Errors > 0 && source.Fetched == 0)
            {
                builder.Append("failed");
            }
            else
            {
                builder.Append(source.Stored).Append(" new");
                if (source.Duplicates > 0)
                {
                    builder.Append(" · ").Append(source.Duplicates).Append(" known");
                }

                if (source.Errors > 0)
                {
                    builder.Append(" · ").Append(source.Errors).Append(" errors");
                }
            }

            builder.Append('\n');
        }

        var highlights = report.Sources
            .SelectMany(s => s.TopNew)
            .OrderByDescending(h => h.Score)
            .Take(MaxHighlights)
            .ToList();

        if (highlights.Count > 0)
        {
            builder.Append("\n<b>").Append(Ui.Best).Append(" Worth a look</b>\n");
            var rank = 1;
            foreach (var highlight in highlights)
            {
                var title = WebUtility.HtmlEncode(Shorten(highlight.Title));

                builder.Append(rank++).Append(". ");
                if (highlight.Url is { Length: > 0 } url)
                {
                    builder.Append("<a href=\"").Append(url).Append("\">").Append(title).Append("</a>");
                }
                else
                {
                    builder.Append(title);
                }

                builder.Append(" — ").Append(highlight.Score).Append(" pts, ")
                    .Append(highlight.CommentCount).Append(" comments\n");
            }
        }
        else if (report.TotalStored == 0)
        {
            builder.Append("\nNothing new this cycle.\n");
        }

        return builder.ToString().TrimEnd();
    }

    private static string Shorten(string title) =>
        title.Length <= MaxTitleLength ? title : title[..(MaxTitleLength - 1)] + "…";
}
