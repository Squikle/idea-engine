using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Sources.FourChan;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IdeaEngine.Tests.Sources;

public sealed class FourChanAdapterTests
{
    private const string DiyCatalog = """
        [{"page":1,"threads":[
          {"no":100,"sub":"STICKY","com":"rules","time":1785650000,"replies":500,"sticky":1},
          {"no":101,"com":"<p>Anyone know a cheap way to make custom enclosures? I&#x27;ve been quoted $300</p>",
           "time":1785650100,"replies":45,"last_modified":1785700000},
          {"no":102,"sub":"dead thread","com":"x","time":1785650200,"replies":5}
        ]}]
        """;

    private const string Thread101 = """
        {"posts":[
          {"no":101,"time":1785650100,"com":"<p>Anyone know a cheap way to make custom enclosures? I&#x27;ve been quoted $300</p>","name":"Anonymous","replies":45},
          {"no":1011,"time":1785650300,"com":"Use a resin printer, works great for enclosures honestly","name":"Anonymous"},
          {"no":1012,"time":1785650400,"com":"ok"}
        ]}
        """;

    private static FourChanAdapter CreateAdapter(out StubHttpMessageHandler stub)
    {
        stub = new StubHttpMessageHandler()
            .Map("diy/catalog.json", DiyCatalog)
            .Map("diy/thread/101.json", Thread101)
            .Map("g/catalog.json", "[]");

        var options = new FourChanOptions { PolitenessDelayMs = 0 };
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://a.4cdn.org/") };
        return new FourChanAdapter(
            httpClient,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero)),
            Options.Create(options),
            NullLogger<FourChanAdapter>.Instance);
    }

    [Fact]
    public async Task FetchAsync_SkipsStickiesAndLowReplyThreads_MapsTheRest()
    {
        var adapter = CreateAdapter(out _);

        var items = await adapter.FetchAsync(new SourceFetchOptions(), CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(SourceKind.FourChan, item.Source);
        Assert.Equal("diy/101", item.ExternalId);
        Assert.StartsWith("Anyone know a cheap way to make custom enclosures?", item.Title, StringComparison.Ordinal);
        Assert.Equal("diy", item.Community);
        Assert.Equal(45, item.Score);
        Assert.Equal("https://boards.4chan.org/diy/thread/101", item.Url);
    }

    [Fact]
    public async Task FetchAsync_FiltersShortReplies()
    {
        var adapter = CreateAdapter(out _);

        var items = await adapter.FetchAsync(new SourceFetchOptions(), CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        var comment = Assert.Single(items[0].Comments);
        Assert.Contains("resin printer", comment.Text, StringComparison.Ordinal);
    }
}
