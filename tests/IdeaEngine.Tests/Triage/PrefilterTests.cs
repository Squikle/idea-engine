using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Persistence.Entities;
using IdeaEngine.Infrastructure.Triage;

namespace IdeaEngine.Tests.Triage;

public sealed class PrefilterTests
{
    private static RawItemEntity Item(string title) => new()
    {
        Source = SourceKind.HackerNews,
        ExternalId = "x",
        Title = title,
        ContentHash = "h",
    };

    [Theory]
    [InlineData("Why are hearing aids still $4000 in 2026?")]
    [InlineData("Anyone know a cheap way to make custom enclosures?")]
    public void ShouldAnalyze_NormalPosts_Pass(string title)
    {
        Assert.True(Prefilter.ShouldAnalyze(Item(title), out _));
    }

    [Theory]
    [InlineData("short title")]
    [InlineData("Weekly Discussion Thread - August 2026")]
    [InlineData("MEGATHREAD: everything goes here")]
    [InlineData("[Giveaway] win a 3d printer")]
    [InlineData("Daily Thread: questions and answers")]
    public void ShouldAnalyze_Junk_Rejected(string title)
    {
        Assert.False(Prefilter.ShouldAnalyze(Item(title), out var reason));
        Assert.NotNull(reason);
    }
}
