using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace IdeaEngine.Infrastructure.Research;

/// <summary>
/// eBay Browse marketplace probe for physical-product research (the PowderPal class of
/// miss). Env-guarded: without EBAY_CLIENT_ID/EBAY_CLIENT_SECRET it reports unconfigured
/// and research silently relies on the site:amazon Brave probes instead.
/// </summary>
public sealed class EbayProbeClient(
    HttpClient httpClient,
    ILogger<EbayProbeClient> logger)
{
    private static readonly SemaphoreSlim TokenLock = new(1, 1);

    private static (string Token, DateTimeOffset ExpiresAt)? _token;

    private string? ClientId { get; init; } = Environment.GetEnvironmentVariable("EBAY_CLIENT_ID");

    private string? ClientSecret { get; init; } = Environment.GetEnvironmentVariable("EBAY_CLIENT_SECRET");

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>Top listings for a product query: (title, price, url). Empty on any failure.</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return [];
        }

        try
        {
            var token = await GetTokenAsync(cancellationToken);
            if (token is null)
            {
                return [];
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"https://api.ebay.com/buy/browse/v1/item_summary/search?q={Uri.EscapeDataString(query)}&limit={limit}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("eBay search HTTP {Status}", (int)response.StatusCode);
                return [];
            }

            var payload = await response.Content.ReadFromJsonAsync<SearchEnvelope>(
                Ai.LlmJson.Options, cancellationToken);
            return [.. (payload?.ItemSummaries ?? [])
                .Where(i => i.Title is { Length: > 0 })
                .Select(i => new SearchHit(
                    i.Title!,
                    i.ItemWebUrl ?? "https://www.ebay.com",
                    i.Price is { } price ? $"listed at {price.Value} {price.Currency} on eBay" : "listed on eBay"))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "eBay probe failed for '{Query}'", query);
            return [];
        }
    }

    private async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        await TokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return cached.Token;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post, new Uri("https://api.ebay.com/identity/v1/oauth2/token"));
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = "https://api.ebay.com/oauth/api_scope",
            });

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("eBay token HTTP {Status} — check EBAY_CLIENT_ID/SECRET", (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<TokenEnvelope>(
                Ai.LlmJson.Options, cancellationToken);
            if (payload?.AccessToken is not { Length: > 0 } token)
            {
                return null;
            }

            _token = (token, DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn));
            return token;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private sealed record TokenEnvelope(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] long ExpiresIn);

    private sealed record SearchEnvelope(
        [property: JsonPropertyName("itemSummaries")] IReadOnlyList<ItemSummary>? ItemSummaries);

    private sealed record ItemSummary(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("itemWebUrl")] string? ItemWebUrl,
        [property: JsonPropertyName("price")] PriceBody? Price);

    private sealed record PriceBody(
        [property: JsonPropertyName("value")] string? Value,
        [property: JsonPropertyName("currency")] string? Currency);
}
