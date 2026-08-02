using IdeaEngine.Infrastructure.Research;

namespace IdeaEngine.Tests.Research;

public sealed class ResearchPromptsTests
{
    [Fact]
    public void BuildIdeaContext_IncludesQuestionsAndEvidence()
    {
        var context = ResearchPrompts.BuildIdeaContext(
            "OTC hearing aid tuning service",
            "Remote tuning for cheap OTC hearing aids.",
            "service",
            "seniors with mild hearing loss",
            "$49 per tuning session",
            2,
            ["Who currently offers remote hearing aid tuning?"],
            ["Hearing aids cost $4000 while users would pay $500"]);

        Assert.Contains("Idea: OTC hearing aid tuning service", context, StringComparison.Ordinal);
        Assert.Contains("? Who currently offers remote hearing aid tuning?", context, StringComparison.Ordinal);
        Assert.Contains("- Hearing aids cost $4000", context, StringComparison.Ordinal);
        Assert.Contains("effort 2", context, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSynthesisMessage_NumbersQueriesAndListsHits()
    {
        var message = ResearchPrompts.BuildSynthesisMessage(
            "Idea: X\n",
            [
                ("otc hearing aid tuning app", [new SearchHit("Tuner Pro", "https://example.com/t", "Remote tuning app")]),
                ("empty query", []),
            ]);

        Assert.Contains("[Q1] otc hearing aid tuning app", message, StringComparison.Ordinal);
        Assert.Contains("- Tuner Pro | https://example.com/t | Remote tuning app", message, StringComparison.Ordinal);
        Assert.Contains("[Q2] empty query", message, StringComparison.Ordinal);
        Assert.Contains("(no results)", message, StringComparison.Ordinal);
    }

    [Fact]
    public void SynthesisSystem_DemandsGroundingAndNoMoralizing()
    {
        Assert.Contains("never invent", ResearchPrompts.SynthesisSystem, StringComparison.Ordinal);
        Assert.Contains("Do not moralize", ResearchPrompts.SynthesisSystem, StringComparison.Ordinal);
        Assert.Contains("ONLY from the provided results", ResearchPrompts.SynthesisSystem, StringComparison.Ordinal);
    }
}
