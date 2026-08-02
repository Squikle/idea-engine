using IdeaEngine.Core.Common;

namespace IdeaEngine.Tests.Common;

public sealed class HtmlTextTests
{
    [Fact]
    public void ToPlainText_StripsTagsAndDecodesEntities()
    {
        var html = "<p>I can&#x27;t find a <b>cheap</b> tool &amp; it annoys me</p>";

        Assert.Equal("I can't find a cheap tool & it annoys me", HtmlText.ToPlainText(html));
    }

    [Fact]
    public void ToPlainText_ConvertsBreaksToNewlines()
    {
        var html = "line one<br>line two<br/>line three";

        Assert.Equal("line one\nline two\nline three", HtmlText.ToPlainText(html));
    }

    [Fact]
    public void ToPlainText_CollapsesWhitespace()
    {
        var html = "<div>  too   many\t\tspaces  </div><div></div><div>next</div>";

        Assert.Equal("too many spaces\nnext", HtmlText.ToPlainText(html));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToPlainText_EmptyInput_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, HtmlText.ToPlainText(input));
    }
}
