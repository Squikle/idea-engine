using System.Net;
using System.Text.RegularExpressions;

namespace IdeaEngine.Core.Common;

/// <summary>
/// Minimal HTML-to-text cleanup for source payloads (HN/4chan/RSS bodies and comments).
/// Not a sanitizer - output is only ever fed to analysis, never rendered.
/// </summary>
public static partial class HtmlText
{
    /// <summary>Strips tags, decodes entities, collapses whitespace.</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        // <br> and paragraph-ish boundaries become newlines so sentences don't glue together.
        var text = LineBreakTags().Replace(html, "\n");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = SpacesAndTabs().Replace(text, " ");
        text = SpaceAroundNewlines().Replace(text, "\n");
        text = ExcessNewlines().Replace(text, "\n");
        return text.Trim();
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/li)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTags();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex("[ \\t]+")]
    private static partial Regex SpacesAndTabs();

    [GeneratedRegex(" ?\\r?\\n ?")]
    private static partial Regex SpaceAroundNewlines();

    [GeneratedRegex("\\n{2,}")]
    private static partial Regex ExcessNewlines();
}
