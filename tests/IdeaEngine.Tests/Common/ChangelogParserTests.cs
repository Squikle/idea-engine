using IdeaEngine.Core.Common;

namespace IdeaEngine.Tests.Common;

public sealed class ChangelogParserTests
{
    private const string Changelog =
        """
        # Changelog

        Intro text.

        ## 0.5.0 — 2026-08-02

        - startup patchnotes
        - /best command

        ## 0.4.0 — 2026-08-02

        - ideation sessions
        """;

    [Fact]
    public void TryGetSection_TopVersion_StopsAtNextHeading()
    {
        var section = ChangelogParser.TryGetSection(Changelog, "0.5.0");

        Assert.NotNull(section);
        Assert.Contains("startup patchnotes", section, StringComparison.Ordinal);
        Assert.DoesNotContain("ideation sessions", section, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetSection_LastVersion_ReadsToEnd()
    {
        var section = ChangelogParser.TryGetSection(Changelog, "0.4.0");

        Assert.Equal("- ideation sessions", section);
    }

    [Theory]
    [InlineData("9.9.9")]
    [InlineData("")]
    public void TryGetSection_Missing_ReturnsNull(string version)
    {
        Assert.Null(ChangelogParser.TryGetSection(Changelog, version));
    }
}
