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

    [Fact]
    public void TryParse_TrailingProseAfterObject_StartingWithBrace()
    {
        // Job #52 failure shape: valid-looking object followed by model commentary.
        var result = LlmJson.TryParse<Sample>(
            "{\"name\":\"x\",\"value\":3}\n\nNote: I kept the summary brief as requested.");

        Assert.NotNull(result);
        Assert.Equal(3, result!.Value);
    }

    [Fact]
    public void TryParse_RawNewlineInsideString_Escaped()
    {
        var result = LlmJson.TryParse<Sample>("{\"name\":\"line one\nline two\",\"value\":9}");

        Assert.NotNull(result);
        Assert.Equal("line one\nline two", result!.Name);
        Assert.Equal(9, result.Value);
    }

    [Fact]
    public void TryParse_RawTabAndCr_InsideString()
    {
        var result = LlmJson.TryParse<Sample>("{\"name\":\"a\tb\rc\",\"value\":1}");

        Assert.NotNull(result);
        Assert.Equal(1, result!.Value);
    }

    [Fact]
    public void TryParse_BracesInsideStrings_DoNotConfuseExtraction()
    {
        var result = LlmJson.TryParse<Sample>(
            "prefix {\"name\":\"has } and { inside\",\"value\":4} suffix");

        Assert.NotNull(result);
        Assert.Equal("has } and { inside", result!.Name);
    }

    [Fact]
    public void TryParse_ProseAndControlChars_Combined()
    {
        var result = LlmJson.TryParse<Sample>(
            "Sure! Here you go:\n```json\n{\"name\":\"multi\nline\",\"value\":2}\n```\nLet me know!");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Value);
    }

    [Fact]
    public void TryParse_EscapedQuotesInsideStrings_StillBalanced()
    {
        var result = LlmJson.TryParse<Sample>(
            "{\"name\":\"she said \\\"hi\\\" {x}\",\"value\":6} trailing");

        Assert.NotNull(result);
        Assert.Equal("she said \"hi\" {x}", result!.Name);
    }
}
