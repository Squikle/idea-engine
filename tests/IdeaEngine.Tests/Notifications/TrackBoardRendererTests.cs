using IdeaEngine.Core.Notifications;
using IdeaEngine.Infrastructure.Notifications;

namespace IdeaEngine.Tests.Notifications;

public sealed class TrackBoardRendererTests
{
    private static readonly DateTimeOffset Started = new(2026, 8, 2, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 6, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Render_ShowsEveryTrack_ActiveAndIdleStates()
    {
        var snapshot = new StatusSnapshot(
            new Dictionary<string, TrackState>
            {
                [Tracks.Collect] = new(true, "HackerNews…", Now.AddMinutes(-1), null, null, null),
                [Tracks.Analyze] = new(false, null, null, "+9 signals from 24", Now.AddMinutes(-10), null),
                [Tracks.Ideate] = new(false, null, null, "3🟢 0☠️ · $0.08", Now.AddHours(-2), Now.AddHours(4)),
            },
            Started);

        var html = TrackBoardRenderer.Render(snapshot, Now, TimeZoneInfo.Utc);

        Assert.Contains("🟢 idea-engine</b> · up 2h 30m", html, StringComparison.Ordinal);
        Assert.Contains("📥 collect: ⏳ HackerNews…", html, StringComparison.Ordinal);
        Assert.Contains("🧠 analyze: last 06:20 · +9 signals from 24", html, StringComparison.Ordinal);
        Assert.Contains("💡 ideate: last 04:30 · 3🟢 0☠️ · $0.08 · next 10:30", html, StringComparison.Ordinal);
        Assert.Contains("🔎 research: —", html, StringComparison.Ordinal); // untouched track still visible
        Assert.Contains("🗞 digest: —", html, StringComparison.Ordinal);
        Assert.Contains("upd 06:30:00 UTC", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EscapesHtmlInDetails()
    {
        var snapshot = new StatusSnapshot(
            new Dictionary<string, TrackState>
            {
                [Tracks.Research] = new(true, "#5 <script>", Now, null, null, null),
            },
            Started);

        var html = TrackBoardRenderer.Render(snapshot, Now, TimeZoneInfo.Utc);

        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ExtraTracks_AppearAutomatically()
    {
        var snapshot = new StatusSnapshot(
            new Dictionary<string, TrackState>
            {
                [Tracks.Sweep] = new(false, null, null, "23 scanned · 2 queued", Now.AddHours(-1), Now.AddDays(29)),
                ["somefuturetrack"] = new(true, "working…", Now, null, null, null),
            },
            Started);

        var html = TrackBoardRenderer.Render(snapshot, Now, TimeZoneInfo.Utc);

        Assert.Contains("🔄 sweep: last 05:30 · 23 scanned · 2 queued", html, StringComparison.Ordinal);
        Assert.Contains("somefuturetrack: ⏳ working…", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderOffline_ShowsReason()
    {
        var html = TrackBoardRenderer.RenderOffline("shutdown", Now, TimeZoneInfo.Utc);

        Assert.Contains("🔴 idea-engine — OFFLINE", html, StringComparison.Ordinal);
        Assert.Contains("shutdown", html, StringComparison.Ordinal);
    }
}
