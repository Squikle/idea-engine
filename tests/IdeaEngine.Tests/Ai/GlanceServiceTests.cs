using IdeaEngine.Infrastructure.Ai;

namespace IdeaEngine.Tests.Ai;

public sealed class GlanceServiceTests
{
    [Fact]
    public void BuildUserMessage_OneLinePerSignal_WithIdAndKind()
    {
        var message = GlanceService.BuildUserMessage(
        [
            new GlanceInput(12, "Hearing aids cost $4000 while users would pay $500", "pain"),
            new GlanceInput(34, "Makers want LED earrings that sync with music", "wish"),
        ]);

        Assert.Contains("12: Hearing aids cost $4000", message, StringComparison.Ordinal);
        Assert.Contains("(pain)", message, StringComparison.Ordinal);
        Assert.Contains("34: Makers want LED earrings", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResponse_MapsIdsAndTrims()
    {
        var parsed = GlanceService.ParseResponse(
            """{"glances":[{"id":12,"text":"  $4000 hearing aids vs $500 willingness  "},{"id":0,"text":"junk"},{"id":34,"text":""}]}""");

        var entry = Assert.Single(parsed);
        Assert.Equal(12, entry.Key);
        Assert.Equal("$4000 hearing aids vs $500 willingness", entry.Value);
    }

    [Fact]
    public void ParseResponse_Garbage_ReturnsEmpty()
    {
        Assert.Empty(GlanceService.ParseResponse("not json at all"));
        Assert.Empty(GlanceService.ParseResponse(null));
    }
}
