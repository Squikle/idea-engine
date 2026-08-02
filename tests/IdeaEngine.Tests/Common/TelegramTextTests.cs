using IdeaEngine.Core.Common;

namespace IdeaEngine.Tests.Common;

public sealed class TelegramTextTests
{
    [Fact]
    public void FromChangelogSection_UnwrapsHardWrappedLines()
    {
        var section =
            """
            - `/research <id>` - the final validation stage closing the loop: plans 4-8 web
              queries from the skeptic's open questions
            - second bullet
            """;

        var html = TelegramText.FromChangelogSection(section);
        var lines = html.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Contains("plans 4-8 web queries from the skeptic", lines[0], StringComparison.Ordinal);
        Assert.Equal("• second bullet", lines[1]);
    }

    [Fact]
    public void FromChangelogSection_EscapesHtmlAndRendersCodeSpans()
    {
        var html = TelegramText.FromChangelogSection("- `/idea <id>` shows **verdict** & more");

        Assert.Contains("<code>/idea &lt;id&gt;</code>", html, StringComparison.Ordinal);
        Assert.Contains("verdict &amp; more", html, StringComparison.Ordinal);
        Assert.DoesNotContain("**", html, StringComparison.Ordinal);
    }
}
