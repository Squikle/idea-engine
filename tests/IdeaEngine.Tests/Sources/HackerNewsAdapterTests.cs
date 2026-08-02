using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Sources.HackerNews;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IdeaEngine.Tests.Sources;

public sealed class HackerNewsAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private const string FrontPageJson = """
        {"hits":[
          {"objectID":"1001","title":"Show HN: I built a cheap CNC from printer parts","url":"https://example.com/cnc",
           "author":"maker1","points":342,"num_comments":120,"created_at_i":1785650400,
           "story_text":null,"_tags":["story","front_page"]},
          {"objectID":"1002","title":"Why are hearing aids still $4000?","url":null,
           "author":"asker2","points":510,"num_comments":301,"created_at_i":1785636000,
           "story_text":"<p>My grandmother needs them &amp; the prices are insane.</p>","_tags":["story","front_page"]}
        ]}
        """;

    private const string AskHnJson = """
        {"hits":[
          {"objectID":"1002","title":"Why are hearing aids still $4000?","url":null,
           "author":"asker2","points":510,"num_comments":301,"created_at_i":1785636000,
           "story_text":"<p>duplicate of front page hit</p>","_tags":["story","ask_hn"]},
          {"objectID":"1003","title":"Ask HN: Tool to track my 3D printer filament inventory?","url":null,
           "author":"printer3","points":25,"num_comments":40,"created_at_i":1785715200,
           "story_text":"<p>Spreadsheets don&#x27;t cut it anymore</p>","_tags":["story","ask_hn"]}
        ]}
        """;

    private const string CommentsFor1001 = """
        {"hits":[
          {"objectID":"c1","comment_text":"I&#x27;d pay for a <b>kit version</b> of this","author":"buyer","points":45,
           "created_at_i":1785651000,"_tags":["comment"]},
          {"objectID":"c2","comment_text":"","author":"empty","points":2,"created_at_i":1785651100,"_tags":["comment"]}
        ]}
        """;

    private const string EmptyHits = """{"hits":[]}""";

    private static (HackerNewsAdapter Adapter, StubHttpMessageHandler Stub) CreateAdapter()
    {
        var stub = new StubHttpMessageHandler()
            .Map("search?tags=front_page", FrontPageJson)
            .Map("search_by_date?tags=ask_hn", AskHnJson)
            .Map("tags=comment,story_1001", CommentsFor1001)
            .Map("tags=comment,story_1002", EmptyHits)
            .Map("tags=comment,story_1003", EmptyHits);

        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://hn.algolia.com/api/v1/") };
        var adapter = new HackerNewsAdapter(
            httpClient,
            new FakeTimeProvider(Now),
            Options.Create(new HackerNewsOptions()),
            NullLogger<HackerNewsAdapter>.Instance);

        return (adapter, stub);
    }

    [Fact]
    public async Task FetchAsync_MergesFeedsAndDeduplicatesById()
    {
        var (adapter, _) = CreateAdapter();

        var items = await adapter.FetchAsync(new SourceFetchOptions(), CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        Assert.Equal(3, items.Count);
        Assert.Equal(["1001", "1002", "1003"], items.Select(i => i.ExternalId));
    }

    [Fact]
    public async Task FetchAsync_MapsFieldsAndStripsHtml()
    {
        var (adapter, _) = CreateAdapter();

        var items = await adapter.FetchAsync(new SourceFetchOptions(), CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        var hearingAids = items.Single(i => i.ExternalId == "1002");
        Assert.Equal(SourceKind.HackerNews, hearingAids.Source);
        Assert.Equal("My grandmother needs them & the prices are insane.", hearingAids.Body);
        Assert.Equal("https://news.ycombinator.com/item?id=1002", hearingAids.Url);
        Assert.Equal("front_page", hearingAids.Community);
        Assert.Equal(510, hearingAids.Score);
        Assert.Equal(Now, hearingAids.FetchedAt);

        var cnc = items.Single(i => i.ExternalId == "1001");
        Assert.Equal("https://example.com/cnc", cnc.Url);
        var comment = Assert.Single(cnc.Comments);
        Assert.Equal("I'd pay for a kit version of this", comment.Text);
        Assert.Equal(45, comment.Score);
    }

    [Fact]
    public async Task FetchAsync_TagsAskHnCommunity()
    {
        var (adapter, _) = CreateAdapter();

        var items = await adapter.FetchAsync(new SourceFetchOptions(), CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        Assert.Equal("ask_hn", items.Single(i => i.ExternalId == "1003").Community);
    }

    [Fact]
    public async Task FetchAsync_RespectsMaxItems()
    {
        var (adapter, _) = CreateAdapter();

        var items = await adapter
            .FetchAsync(new SourceFetchOptions { MaxItems = 1 }, CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        Assert.Single(items);
    }

    [Fact]
    public async Task FetchAsync_SkipsItemsOlderThanSince()
    {
        var (adapter, _) = CreateAdapter();
        var since = DateTimeOffset.FromUnixTimeSeconds(1785700000); // only 1003 is newer

        var items = await adapter
            .FetchAsync(new SourceFetchOptions { Since = since }, CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        Assert.Equal(["1003"], items.Select(i => i.ExternalId));
    }

    [Fact]
    public async Task FetchAsync_SurvivesFailingQueries()
    {
        var stub = new StubHttpMessageHandler(); // everything 404s
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://hn.algolia.com/api/v1/") };
        var adapter = new HackerNewsAdapter(
            httpClient,
            new FakeTimeProvider(Now),
            Options.Create(new HackerNewsOptions()),
            NullLogger<HackerNewsAdapter>.Instance);

        var items = await adapter.FetchAsync(new SourceFetchOptions(), CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        Assert.Empty(items);
    }
}
