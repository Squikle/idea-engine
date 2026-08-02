using System.Globalization;
using System.Text;
using IdeaEngine.Infrastructure.Ideation;

namespace IdeaEngine.Worker;

/// <summary>Ideation batch results HTML, shared by /ideate and the autopilot.</summary>
internal static class IdeationFormatting
{
    public static string BuildResultsHtml(IdeationBatchResult result)
    {
        var builder = new StringBuilder("<b>💡 Ideation results</b>\n");
        foreach (var line in result.Lines)
        {
            builder.Append(System.Net.WebUtility.HtmlEncode(line)).Append('\n');
        }

        if (result.StoppedReason is { } reason)
        {
            builder.Append("\nStopped: ").Append(System.Net.WebUtility.HtmlEncode(reason));
        }

        builder.Append('\n').Append(result.Advanced).Append(" live · ")
            .Append(result.Killed).Append(" killed · $")
            .Append(result.CostUsd.ToString("F2", CultureInfo.InvariantCulture))
            .Append("\n/ideas for ratings · /research id to validate");

        return builder.ToString();
    }
}
