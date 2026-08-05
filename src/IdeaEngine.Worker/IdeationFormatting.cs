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

        builder.Append('\n').Append(result.Advanced).Append(" idea(s) survived the skeptic · ")
            .Append(result.Killed).Append(" killed · $")
            .Append(result.CostUsd.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');

        if (result.PoolEligible > 0)
        {
            builder.Append("\n<i>How this works: each attempt = ONE idea. The builder reads ")
                .Append(result.SampledPerSession).Append(" randomly-drawn signals from the eligible pool (")
                .Append(result.PoolEligible)
                .Append(" top+long-tail right now), fuses 2-4 of them (⛓ above) through an auto-rotated ")
                .Append("playbook lens, then the skeptic attacks. Undrawn signals are NOT killed - they ")
                .Append("wait for the next attempt. More at once: /ideate 5 · aim it: /ideate from 123 456 · ")
                .Append("browse fuel: /signals unused</i>");
        }

        builder.Append("\n/ideas for ratings · /research id to validate");
        return builder.ToString();
    }
}
