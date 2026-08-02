using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Sources.RedditRss;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IdeaEngine.Tests.Sources;

public sealed class RedditRssAdapterTests
{
    private const string AtomFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>r/3Dprinting hot</title>
          <entry>
            <id>t3_abc123</id>
            <title>My print farm now pays my rent - AMA</title>
            <link href="https://www.reddit.com/r/3Dprinting/comments/abc123/"/>
            <author><name>/u/printerguy</name></author>
            <published>2026-08-01T09:00:00+00:00</published>
            <content type="html">&lt;p&gt;Started with one Ender 3, now running 12 printers making cable organizers and selling locally.&lt;/p&gt;</content>
          </entry>
          <entry>
            <id>t3_def456</id>
            <title>What part do you wish existed but nobody sells?</title>
            <link href="https://www.reddit.com/r/3Dprinting/comments/def456/"/>
            <author><name>/u/curious</name></author>
            <published>2026-08-01T10:00:00+00:00</published>
            <content type="html">&lt;p&gt;Looking for ideas worth printing. What do you keep hacking together yourself?&lt;/p&gt;</content>
          </entry>
        </feed>
        """;

    private static RedditRssAdapter CreateAdapter()
    {
        var stub = new StubHttpMessageHandler().Map("r/test/hot.rss", AtomFeed);
        var options = new RedditRssOptions { PolitenessDelayMs = 0, PerSubredditLimit = 10 };
        options.Subreddits.Clear();
        options.Subreddits.Add("test");

        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://www.reddit.com/") };
        return new RedditRssAdapter(
            httpClient,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero)),
            Options.Create(options),
            NullLogger<RedditRssAdapter>.Instance);
    }

    [Fact]
    public async Task FetchAsync_ParsesAtomAndUsesPositionAsScoreProxy()
    {
        var adapter = CreateAdapter();

        var items = await adapter.FetchAsync(new SourceFetchOptions(), CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);

        var first = items[0];
        Assert.Equal(SourceKind.RedditRss, first.Source);
        Assert.Equal("t3_abc123", first.ExternalId);
        Assert.Equal("My print farm now pays my rent - AMA", first.Title);
        Assert.Equal("u/printerguy", first.Author);
        Assert.Equal("test", first.Community);
        Assert.Equal(10, first.Score);
        Assert.Contains("12 printers", first.Body, StringComparison.Ordinal);

        Assert.Equal(9, items[1].Score); // next position, lower proxy score
    }
}
