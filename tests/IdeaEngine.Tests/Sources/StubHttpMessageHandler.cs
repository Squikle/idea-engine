using System.Net;
using System.Text;

namespace IdeaEngine.Tests.Sources;

/// <summary>
/// Routes requests to canned responses by URL substring. Unmatched requests return 404
/// so tests fail loudly on unexpected calls.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(string UrlContains, string Json)> _routes = [];

    public List<string> RequestedUrls { get; } = [];

    public StubHttpMessageHandler Map(string urlContains, string json)
    {
        _routes.Add((urlContains, json));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        RequestedUrls.Add(url);

        foreach (var (fragment, json) in _routes)
        {
            if (url.Contains(fragment, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"no stub for: {url}"),
        });
    }
}
