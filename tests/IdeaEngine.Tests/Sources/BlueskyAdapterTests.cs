using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Sources.Bluesky;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IdeaEngine.Tests.Sources;

public sealed class BlueskyAdapterTests
{
    private const string SearchJson = """
        {"posts":[
          {"uri":"at://did:plc:abc/app.bsky.feed.post/xyz9",
           "author":{"handle":"maker.bsky.social","displayName":"Maker"},
           "record":{"text":"someone should make LED earrings that sync with music. i would buy ten pairs immediately","createdAt":"2026-08-01T10:00:00Z"},
           "replyCount":12,"repostCount":2,"likeCount":10},
          {"uri":"at://did:plc:def/app.bsky.feed.post/low1",
           "author":{"handle":"quiet.bsky.social"},
           "record":{"text":"someone should make a thing","createdAt":"2026-08-01T11:00:00Z"},
           "replyCount":0,"repostCount":0,"likeCount":1}
        ]}
        """;

    private static BlueskyAdapter CreateAdapter()
    {
        var stub = new StubHttpMessageHandler().Map("app.bsky.feed.searchPosts", SearchJson);
        var options = new BlueskyOptions { PolitenessDelayMs = 0 };
        options.Queries.Clear();
        options.Queries.Add("someone should make");

        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://public.api.bsky.app/") };
        return new BlueskyAdapter(
            httpClient,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero)),
            Options.Create(options),
            NullLogger<BlueskyAdapter>.Instance);
    }

    [Fact]
    public async Task FetchAsync_FiltersByMinLikes_AndMapsWebUrl()
    {
        var adapter = CreateAdapter();

        var items = await adapter.FetchAsync(new SourceFetchOptions(), CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(SourceKind.Bluesky, item.Source);
        Assert.Equal("at://did:plc:abc/app.bsky.feed.post/xyz9", item.ExternalId);
        Assert.Equal("https://bsky.app/profile/maker.bsky.social/post/xyz9", item.Url);
        Assert.Equal(14, item.Score); // 10 likes + 2*2 reposts
        Assert.Equal(12, item.CommentCount);
        Assert.Equal("q:someone should make", item.Community);
    }
}
