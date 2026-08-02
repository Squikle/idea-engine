using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace IdeaEngine.Core.Common;

/// <summary>
/// Renders free text for Telegram HTML with URLs preserved as short hyperlinks
/// (label = host) instead of being truncated. Clipping never cuts inside a link.
/// </summary>
public static partial class Linkify
{
    /// <param name="maxVisible">Budget for VISIBLE characters (labels count, hrefs don't).</param>
    public static string Render(string? text, int maxVisible)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var visible = 0;
        var lastIndex = 0;
        var clipped = false;

        foreach (Match match in UrlPattern().Matches(text))
        {
            if (!AppendText(builder, text[lastIndex..match.Index], ref visible, maxVisible))
            {
                clipped = true;
                break;
            }

            var url = match.Value.TrimEnd('.', ',', ')', ';');
            var label = HostLabel(url);
            if (visible + label.Length + 2 > maxVisible)
            {
                clipped = true;
                lastIndex = match.Index;
                break;
            }

            builder.Append("<a href=\"").Append(url).Append("\">").Append(WebUtility.HtmlEncode(label)).Append("</a>");
            visible += label.Length;
            lastIndex = match.Index + match.Length;
        }

        if (!clipped && !AppendText(builder, text[lastIndex..], ref visible, maxVisible))
        {
            clipped = true;
        }

        var result = builder.ToString().TrimEnd();
        return clipped ? result + "…" : result;
    }

    private static bool AppendText(StringBuilder builder, string segment, ref int visible, int maxVisible)
    {
        if (segment.Length == 0)
        {
            return true;
        }

        var budget = maxVisible - visible;
        if (budget <= 0)
        {
            return false;
        }

        if (segment.Length <= budget)
        {
            builder.Append(WebUtility.HtmlEncode(segment));
            visible += segment.Length;
            return true;
        }

        var cut = segment[..budget];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > budget - 20 && lastSpace > 0)
        {
            cut = cut[..lastSpace];
        }

        builder.Append(WebUtility.HtmlEncode(cut.TrimEnd()));
        visible += cut.Length;
        return false;
    }

    private static string HostLabel(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host
            : "link";
    }

    [GeneratedRegex(@"https?://[^\s<>""')\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
