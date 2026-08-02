using IdeaEngine.Infrastructure.Notifications;

namespace IdeaEngine.Tests.Notifications;

public sealed class StatusRendererTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 2, 0, 44, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 4, 47, 12, TimeSpan.Zero);

    [Fact]
    public void RenderLive_WithNextCycle_ShowsRelativeWait()
    {
        var next = Now.AddHours(2).AddMinutes(59);

        var text = StatusRenderer.RenderLive("Idle", "last cycle: +96 items", next, StartedAt, Now);

        Assert.Contains("<b>● idea-engine</b>", text, StringComparison.Ordinal);
        Assert.Contains("Idle — last cycle: +96 items", text, StringComparison.Ordinal);
        Assert.Contains("Next cycle: 07:46 UTC (in 2h 59m)", text, StringComparison.Ordinal);
        Assert.Contains("Up since 02 Aug 00:44 UTC · upd 04:47:12", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderLive_WithoutNextCycle_OmitsLine()
    {
        var text = StatusRenderer.RenderLive("Collecting", "FourChan…", null, StartedAt, Now);

        Assert.DoesNotContain("Next cycle", text, StringComparison.Ordinal);
        Assert.Contains("Collecting — FourChan…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderLive_EscapesHtmlInDetail()
    {
        var text = StatusRenderer.RenderLive("Idle", "<script>alert(1)</script>", null, StartedAt, Now);

        Assert.Contains("&lt;script&gt;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderOffline_ShowsReasonAndTimestamp()
    {
        var text = StatusRenderer.RenderOffline("crashed: FormatException", Now);

        Assert.Contains("<b>○ idea-engine — OFFLINE</b>", text, StringComparison.Ordinal);
        Assert.Contains("crashed: FormatException", text, StringComparison.Ordinal);
        Assert.Contains("02 Aug 04:47:12 UTC", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderLive_PastNextCycle_ClampsToZero()
    {
        var text = StatusRenderer.RenderLive("Idle", null, Now.AddMinutes(-5), StartedAt, Now);

        Assert.Contains("(in 0m 00s)", text, StringComparison.Ordinal);
    }
}
