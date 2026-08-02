using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace IdeaEngine.Infrastructure.Research;

/// <summary>One web search hit, trimmed to what the synthesis model needs.</summary>
public sealed record SearchHit(string Title, string Url, string Description);

/// <summary>Thin Brave Web Search client. Never throws; empty results on failure.</summary>
public sealed class BraveSearchClient(
    HttpClient httpClient,
    ILogger<BraveSearchClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => httpClient.DefaultRequestHeaders.Contains("X-Subscription-Token");

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query, int count, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"web/search?q={Uri.EscapeDataString(query)}&count={count}";
            var response = await httpClient.GetFromJsonAsync<BraveResponse>(
                new Uri(url, UriKind.Relative), JsonOptions, cancellationToken);

            return
            [
                .. (response?.Web?.Results ?? [])
                    .Where(r => !string.IsNullOrWhiteSpace(r.Url) && !string.IsNullOrWhiteSpace(r.Title))
                    .Select(r => new SearchHit(
                        r.Title!.Trim(),
                        r.Url!,
                        Truncate(r.Description ?? string.Empty, 260))),
            ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Brave search failed for query '{Query}'", query);
            return [];
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private sealed record BraveResponse([property: JsonPropertyName("web")] BraveWeb? Web);

    private sealed record BraveWeb([property: JsonPropertyName("results")] IReadOnlyList<BraveResult>? Results);

    private sealed record BraveResult(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("description")] string? Description);
}
