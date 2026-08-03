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

    private sealed record Report(string? Verdict, double Confidence);

    [Fact]
    public void TryParse_MissingObjectCloserBeforeArrayClose_Job54Shape()
    {
        // Exact defect from job #54: an object in "answers" never got its closing brace.
        var result = LlmJson.TryParse<Report>(
            """{"verdict":"maybe","confidence":0.55,"answers":[{"q":"a","urls":["https://x.com"]},{"q":"b","urls":["https://y.com"]],"notes":"n"}""");

        Assert.NotNull(result);
        Assert.Equal("maybe", result!.Verdict);
        Assert.Equal(0.55, result.Confidence, 3);
    }

    [Fact]
    public void TryParse_MissingArrayCloserBeforeObjectClose()
    {
        var result = LlmJson.TryParse<Report>(
            """{"verdict":"go","confidence":0.7,"risks":["r1","r2"}""");

        Assert.NotNull(result);
        Assert.Equal("go", result!.Verdict);
    }

    [Fact]
    public void TryParse_TruncatedMidString_ClosedAtEof()
    {
        var result = LlmJson.TryParse<Report>(
            """{"verdict":"maybe","confidence":0.5,"notes":"cut off mid-sent""");

        Assert.NotNull(result);
        Assert.Equal("maybe", result!.Verdict);
    }

    [Fact]
    public void TryParse_UnescapedInnerQuotes_Escaped()
    {
        var result = LlmJson.TryParse<Report>(
            """{"verdict":"no-go","confidence":0.8,"notes":"users said "too pricey" often"}""");

        Assert.NotNull(result);
        Assert.Equal("no-go", result!.Verdict);
    }

    [Fact]
    public void TryParse_ValidJson_UntouchedByRepairLayers()
    {
        // Repair layers must be no-ops on healthy output: full round-trip equality.
        var result = LlmJson.TryParse<Sample>(
            """{"name":"has ] and } in string","value":11}""");

        Assert.NotNull(result);
        Assert.Equal("has ] and } in string", result!.Name);
        Assert.Equal(11, result.Value);
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
