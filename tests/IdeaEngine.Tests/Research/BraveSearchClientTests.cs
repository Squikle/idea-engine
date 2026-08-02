using IdeaEngine.Infrastructure.Research;
using IdeaEngine.Tests.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace IdeaEngine.Tests.Research;

public sealed class BraveSearchClientTests
{
    private const string BraveJson = """
        {"web":{"results":[
          {"title":"Tuner Pro","url":"https://example.com/t","description":"Remote hearing aid tuning"},
          {"title":"","url":"https://example.com/skip","description":"no title -> skipped"},
          {"title":"NoUrl","url":null,"description":"skipped too"}
        ]}}
        """;

    [Fact]
    public async Task SearchAsync_ParsesAndFiltersResults()
    {
        var stub = new StubHttpMessageHandler().Map("web/search", BraveJson);
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://api.search.brave.com/res/v1/") };
        httpClient.DefaultRequestHeaders.Add("X-Subscription-Token", "test");
        var client = new BraveSearchClient(httpClient, NullLogger<BraveSearchClient>.Instance);

        Assert.True(client.IsConfigured);
        var hits = await client.SearchAsync("hearing aid tuning", 5, CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal("Tuner Pro", hit.Title);
        Assert.Equal("https://example.com/t", hit.Url);
    }

    [Fact]
    public async Task SearchAsync_FailureReturnsEmpty()
    {
        var stub = new StubHttpMessageHandler(); // 404 everything
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://api.search.brave.com/res/v1/") };
        var client = new BraveSearchClient(httpClient, NullLogger<BraveSearchClient>.Instance);

        Assert.False(client.IsConfigured);
        Assert.Empty(await client.SearchAsync("anything", 3, CancellationToken.None));
    }
}
