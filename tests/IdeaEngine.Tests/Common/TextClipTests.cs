using IdeaEngine.Core.Common;

namespace IdeaEngine.Tests.Common;

public sealed class TextClipTests
{
    [Fact]
    public void Clip_ShortText_Unchanged()
    {
        Assert.Equal("short", TextClip.Clip("short", 20));
    }

    [Fact]
    public void Clip_BreaksAtWordBoundary_WithEllipsis()
    {
        var clipped = TextClip.Clip("the market is dominated by strong incumbents with network effects", 40);

        Assert.EndsWith("…", clipped, StringComparison.Ordinal);
        Assert.True(clipped.Length <= 40);
        Assert.DoesNotMatch(@"\w…$", clipped.Replace("…", " …")); // no mid-word cut
        Assert.Equal("the market is dominated by strong…", clipped);
    }

    [Fact]
    public void Clip_LongSingleWord_HardCutsWithEllipsis()
    {
        var clipped = TextClip.Clip(new string('x', 100), 20);

        Assert.Equal(20, clipped.Length);
        Assert.EndsWith("…", clipped, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Clip_Empty_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, TextClip.Clip(input, 10));
    }
}
