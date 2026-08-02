using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Sources.Gdelt;

/// <summary>Bound from configuration section <c>IdeaEngine:Sources:Gdelt</c>.</summary>
public sealed class GdeltOptions
{
    /// <summary>label → GDELT DOC 2.0 query. Bad news is opportunity fuel: each pain theme
    /// feeds the "is there a local Citizen-style solution?" arbitrage question.</summary>
    public IDictionary<string, string> Queries { get; } = new Dictionary<string, string>
    {
        ["safety"] = "(carjacking OR burglary OR \"crime wave\") sourcelang:eng tone<-6",
        ["scams"] = "(scam OR phishing OR fraudsters) sourcelang:eng tone<-6",
        ["outages"] = "(outage OR blackout OR \"service down\") sourcelang:eng tone<-5",
        ["dailypain"] = "(\"waiting list\" OR \"can't afford\" OR unaffordable) sourcelang:eng tone<-5",
    };

    public int MaxRecordsPerQuery { get; set; } = 20;

    /// <summary>GDELT window, e.g. 1d/2d.</summary>
    public string Timespan { get; set; } = "2d";

    /// <summary>GDELT enforces >=5s between requests and penalty-boxes violators for a while.</summary>
    public int PolitenessDelayMs { get; set; } = 10000;
}

/// <summary>
/// GDELT DOC 2.0 — the free global news firehose, filtered to high-pain, negative-tone
/// themes. Headlines only (no body): triage judges the pain, the arbitrage lens asks
/// whether a garage-scale fix exists for that geography.
/// </summary>
public sealed class GdeltAdapter(
    HttpClient httpClient,
    TimeProvider timeProvider,
    IOptions<GdeltOptions> adapterOptions,
    ILogger<GdeltAdapter> logger) : ISourceAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SourceKind Kind => SourceKind.Gdelt;

    public async IAsyncEnumerable<RawItem> FetchAsync(
        SourceFetchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = adapterOptions.Value;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var yielded = 0;
        var boxed = false;

        foreach (var (label, query) in config.Queries)
        {
            if (yielded >= options.MaxItems || boxed)
            {
                yield break;
            }

            await Task.Delay(config.PolitenessDelayMs, cancellationToken);
            var (articles, throttled) = await SearchAsync(query, config, cancellationToken);
            if (throttled)
            {
                // One 429 means the penalty box - more requests only extend the sentence.
                logger.LogInformation("GDELT penalty box detected; remaining labels wait for the next cycle");
                boxed = true;
                continue;
            }

            foreach (var article in articles)
            {
                if (yielded >= options.MaxItems)
                {
                    yield break;
                }

                var title = WebUtility.HtmlDecode(article.Title ?? string.Empty).Trim();
                if (article.Url is not { Length: > 0 } url || title.Length < 20)
                {
                    continue;
                }

                // Urls exceed the 128-char ExternalId budget; the content hash is stable and fits.
                var externalId = ContentHasher.Compute(url);
                if (!seen.Add(externalId))
                {
                    continue;
                }

                yielded++;
                yield return new RawItem
                {
                    Source = SourceKind.Gdelt,
                    ExternalId = externalId,
                    Title = title,
                    Body = null,
                    Url = url,
                    Author = article.Domain,
                    Community = $"gdelt:{label}",
                    Score = 5, // flat: GDELT has no engagement metric; triage judges the pain
                    CommentCount = 0,
                    CreatedAt = ParseSeenDate(article.SeenDate),
                    FetchedAt = timeProvider.GetUtcNow(),
                };
            }
        }
    }

    private async Task<(IReadOnlyList<GdeltArticle> Articles, bool Throttled)> SearchAsync(
        string query, GdeltOptions config, CancellationToken cancellationToken)
    {
        try
        {
            var url = "doc?query=" + Uri.EscapeDataString(query) +
                $"&mode=ArtList&format=json&maxrecords={config.MaxRecordsPerQuery}" +
                $"&timespan={config.Timespan}&sort=hybridrel";
            using var response = await httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return ([], true);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "GDELT {Status} for query '{Query}' — skipping this label this cycle",
                    (int)response.StatusCode, TextClip.Clip(query, 40));
                return ([], false);
            }

            var payload = await response.Content.ReadFromJsonAsync<GdeltResponse>(JsonOptions, cancellationToken);
            return (payload?.Articles ?? [], false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException
            or Polly.Timeout.TimeoutRejectedException)
        {
            logger.LogWarning(ex, "GDELT fetch failed for '{Query}'", TextClip.Clip(query, 40));
            return ([], false);
        }
    }

    private DateTimeOffset ParseSeenDate(string? seenDate) =>
        DateTimeOffset.TryParseExact(
            seenDate, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : timeProvider.GetUtcNow();

    private sealed record GdeltResponse(
        [property: JsonPropertyName("articles")] IReadOnlyList<GdeltArticle>? Articles);

    private sealed record GdeltArticle(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("seendate")] string? SeenDate,
        [property: JsonPropertyName("domain")] string? Domain,
        [property: JsonPropertyName("sourcecountry")] string? SourceCountry);
}
