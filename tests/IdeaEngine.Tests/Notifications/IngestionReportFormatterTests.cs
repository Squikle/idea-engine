using IdeaEngine.Core.Pipeline;
using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Notifications;

namespace IdeaEngine.Tests.Notifications;

public sealed class IngestionReportFormatterTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 2, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Format_RendersCountsAndTopItems()
    {
        var report = new IngestionCycleReport(When,
        [
            new SourceIngestResult(SourceKind.HackerNews, 55, 41, 14, 0, 18.2,
            [
                new IngestedHighlight("Why are hearing aids still $4000?", "https://example.com/1", 510, 301),
                new IngestedHighlight("Show HN: cheap CNC", "https://example.com/2", 342, 120),
            ]),
        ]);

        var html = IngestionReportFormatter.Format(report, TimeZoneInfo.Utc);

        Assert.Contains("<b>📥 Collected · 02 Aug 14:30 UTC</b>", html, StringComparison.Ordinal);
        Assert.Contains("HackerNews: 41 new · 14 known", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.com/1\">Why are hearing aids still $4000?</a> — 510 pts, 301 comments",
            html, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_OrdersHighlightsByScoreAcrossSources_AndCapsAtFive()
    {
        var hnItems = Enumerable.Range(1, 4)
            .Select(i => new IngestedHighlight($"hn {i}", null, 100 + i, i))
            .ToList();
        var chanItems = Enumerable.Range(1, 4)
            .Select(i => new IngestedHighlight($"chan {i}", null, 200 + i, i))
            .ToList();

        var report = new IngestionCycleReport(When,
        [
            new SourceIngestResult(SourceKind.HackerNews, 4, 4, 0, 0, 1, hnItems),
            new SourceIngestResult(SourceKind.FourChan, 4, 4, 0, 0, 1, chanItems),
        ]);

        var html = IngestionReportFormatter.Format(report, TimeZoneInfo.Utc);

        Assert.Contains("1. chan 4", html, StringComparison.Ordinal);
        Assert.Contains("5. hn 4", html, StringComparison.Ordinal);
        Assert.DoesNotContain("hn 1", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_EscapesHtmlInTitles()
    {
        var report = new IngestionCycleReport(When,
        [
            new SourceIngestResult(SourceKind.HackerNews, 1, 1, 0, 0, 1,
                [new IngestedHighlight("Tool <b>for & by</b> makers", null, 10, 1)]),
        ]);

        var html = IngestionReportFormatter.Format(report, TimeZoneInfo.Utc);

        Assert.Contains("Tool &lt;b&gt;for &amp; by&lt;/b&gt; makers", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_EmptyCycle_SaysNothingNew()
    {
        var report = new IngestionCycleReport(When,
        [
            new SourceIngestResult(SourceKind.HackerNews, 20, 0, 20, 0, 3, []),
        ]);

        var html = IngestionReportFormatter.Format(report, TimeZoneInfo.Utc);

        Assert.Contains("Nothing new this cycle.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_FailedSource_IsMarked()
    {
        var report = new IngestionCycleReport(When,
        [
            new SourceIngestResult(SourceKind.HackerNews, 0, 0, 0, 1, 0.4, []),
        ]);

        var html = IngestionReportFormatter.Format(report, TimeZoneInfo.Utc);

        Assert.Contains("HackerNews: failed", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_LongTitles_AreShortened()
    {
        var longTitle = new string('x', 200);
        var report = new IngestionCycleReport(When,
        [
            new SourceIngestResult(SourceKind.HackerNews, 1, 1, 0, 0, 1,
                [new IngestedHighlight(longTitle, null, 10, 1)]),
        ]);

        var html = IngestionReportFormatter.Format(report, TimeZoneInfo.Utc);

        Assert.Contains("xxx…", html, StringComparison.Ordinal);
        Assert.DoesNotContain(longTitle, html, StringComparison.Ordinal);
    }
}
