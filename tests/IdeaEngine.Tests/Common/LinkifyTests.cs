using IdeaEngine.Core.Common;

namespace IdeaEngine.Tests.Common;

public sealed class LinkifyTests
{
    [Fact]
    public void Render_ConvertsUrlToShortAnchor()
    {
        var html = Linkify.Render(
            "Robin costs $500/mo, see https://www.robinpowered.com/pricing for details", 200);

        Assert.Contains("<a href=\"https://www.robinpowered.com/pricing\">robinpowered.com</a>", html, StringComparison.Ordinal);
        Assert.Contains("Robin costs $500/mo", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NeverCutsInsideLink_ClipsTextInstead()
    {
        var longText = new string('a', 300) + " https://example.com/very/long/path end";

        var html = Linkify.Render(longText, 100);

        Assert.EndsWith("…", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<a href", html[..html.Length].Substring(html.Length - 3), StringComparison.Ordinal);
        Assert.DoesNotContain("https://example.com/very", html, StringComparison.Ordinal); // link dropped whole, not cut
    }

    [Fact]
    public void Render_EscapesHtmlOutsideLinks()
    {
        var html = Linkify.Render("a <b> & c https://x.io/y", 100);

        Assert.Contains("a &lt;b&gt; &amp; c", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://x.io/y\">x.io</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Linkify.Render(null, 50));
        Assert.Equal(string.Empty, Linkify.Render("  ", 50));
    }
}
