using IdeaEngine.Infrastructure.Ai;

namespace IdeaEngine.Tests.Ai;

public sealed class LlmJsonTests
{
    private sealed record Sample(string? Name, int Value);

    [Fact]
    public void TryParse_PlainJson()
    {
        var result = LlmJson.TryParse<Sample>("""{"name":"x","value":3}""");

        Assert.NotNull(result);
        Assert.Equal("x", result!.Name);
        Assert.Equal(3, result.Value);
    }

    [Fact]
    public void TryParse_CodeFencedJson()
    {
        var result = LlmJson.TryParse<Sample>("```json\n{\"name\":\"x\",\"value\":3}\n```");

        Assert.NotNull(result);
        Assert.Equal(3, result!.Value);
    }

    [Fact]
    public void TryParse_LeadingChatter()
    {
        var result = LlmJson.TryParse<Sample>("Here is the JSON you asked for: {\"name\":\"y\",\"value\":7} hope it helps");

        Assert.NotNull(result);
        Assert.Equal("y", result!.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no json here")]
    [InlineData("{broken")]
    public void TryParse_Garbage_ReturnsNull(string? content)
    {
        Assert.Null(LlmJson.TryParse<Sample>(content));
    }

    [Fact]
    public void TryParse_NumbersAsStrings_Tolerated()
    {
        var result = LlmJson.TryParse<Sample>("""{"name":"z","value":"5"}""");

        Assert.NotNull(result);
        Assert.Equal(5, result!.Value);
    }
}
