using IdeaEngine.Core.Sources;

namespace IdeaEngine.Tests.Sources;

public sealed class SourceKindParserTests
{
    [Theory]
    [InlineData("hn", SourceKind.HackerNews)]
    [InlineData("HackerNews", SourceKind.HackerNews)]
    [InlineData("4chan", SourceKind.FourChan)]
    [InlineData("chan", SourceKind.FourChan)]
    [InlineData("bsky", SourceKind.Bluesky)]
    [InlineData("Bluesky", SourceKind.Bluesky)]
    [InlineData("lemmy", SourceKind.Lemmy)]
    [InlineData("reddit", SourceKind.RedditRss)]
    [InlineData(" RSS ", SourceKind.RedditRss)]
    public void TryParse_KnownAliases(string input, SourceKind expected)
    {
        Assert.True(SourceKindParser.TryParse(input, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("myspace")]
    public void TryParse_Unknown_ReturnsFalse(string? input)
    {
        Assert.False(SourceKindParser.TryParse(input, out var kind));
        Assert.Equal(SourceKind.Unknown, kind);
    }
}
