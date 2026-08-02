using IdeaEngine.Infrastructure.Ai;

namespace IdeaEngine.Tests.Ai;

public sealed class IdeationPromptsTests
{
    private static readonly GroundingSignal Signal = new(
        123, "pain", "genuine_need", 0.85,
        "Hearing aids cost $4000 while users would pay $500", "people with hearing loss", "ask_hn");

    [Fact]
    public void BuildGrounding_ContainsCitableIdAndFields()
    {
        var text = IdeationPrompts.BuildGrounding([Signal]);

        Assert.Contains("S123", text, StringComparison.Ordinal);
        Assert.Contains("[pain/genuine_need c0.85]", text, StringComparison.Ordinal);
        Assert.Contains("audience: people with hearing loss", text, StringComparison.Ordinal);
        Assert.Contains("from: ask_hn", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSkepticMessage_IncludesIdeaAndCitedSignals()
    {
        var text = IdeationPrompts.BuildSkepticMessage("""{"title":"OTC hearing aid tuner"}""", [Signal]);

        Assert.Contains("Idea under review:", text, StringComparison.Ordinal);
        Assert.Contains("OTC hearing aid tuner", text, StringComparison.Ordinal);
        Assert.Contains("S123", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompts_DemandJsonOnlyAndStrictness()
    {
        Assert.Contains("ONLY a JSON object", IdeationPrompts.BuilderSystem, StringComparison.Ordinal);
        Assert.Contains("kill", IdeationPrompts.SkepticSystem, StringComparison.Ordinal);
        Assert.Contains("Agreement between AIs is not evidence", IdeationPrompts.SkepticSystem, StringComparison.Ordinal);
        Assert.Contains("No ToS-violating scraping", IdeationPrompts.MetaSystem, StringComparison.Ordinal);
    }
}
