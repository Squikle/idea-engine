using System.Net;
using System.Text.RegularExpressions;

namespace IdeaEngine.Core.Common;

/// <summary>Converts our markdown-ish content into compact Telegram HTML.</summary>
public static partial class TelegramText
{
    /// <summary>
    /// Changelog section → bullet list: unwraps hard-wrapped continuation lines,
    /// escapes HTML, renders `code` spans, strips ** emphasis.
    /// </summary>
    public static string FromChangelogSection(string section)
    {
        var bullets = new List<string>();
        foreach (var rawLine in section.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                bullets.Add(line[2..]);
            }
            else if (bullets.Count > 0)
            {
                bullets[^1] += " " + line; // hard-wrapped continuation
            }
            else
            {
                bullets.Add(line);
            }
        }

        return string.Join('\n', bullets.Select(b => "• " + Render(b)));
    }

    private static string Render(string text)
    {
        var encoded = WebUtility.HtmlEncode(text.Replace("**", string.Empty, StringComparison.Ordinal));
        return CodeSpan().Replace(encoded, "<code>$1</code>");
    }

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex CodeSpan();
}
