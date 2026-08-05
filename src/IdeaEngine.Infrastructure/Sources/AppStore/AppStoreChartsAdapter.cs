using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Sources.AppStore;

/// <summary>Bound from configuration section <c>IdeaEngine:Sources:AppStore</c>.</summary>
public sealed class AppStoreOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Apple marketing-tools RSS, keyless and official.</summary>
    public string BaseUrl { get; set; } = "https://rss.marketingtools.apple.com/api/v2";

    public string Country { get; set; } = "us";

    /// <summary>Charts to snapshot: top-free shows demand, top-paid shows willingness to pay.</summary>
    public IList<string> Charts { get; } = ["top-free", "top-paid"];

    public int PerChart { get; set; } = 25;
}

/// <summary>
/// Daily App Store chart snapshots. One item per (chart, app, day) - dedup makes repeat
/// cycles free; over weeks the corpus becomes trend-delta material (what's climbing).
/// </summary>
public sealed class AppStoreChartsAdapter(
    HttpClient httpClient,
    TimeProvider timeProvider,
    IOptions<AppStoreOptions> adapterOptions,
    ILogger<AppStoreChartsAdapter> logger) : ISourceAdapter
{
    public SourceKind Kind => SourceKind.AppStoreCharts;

    public async IAsyncEnumerable<RawItem> FetchAsync(
        SourceFetchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = adapterOptions.Value;
        if (!config.Enabled)
        {
            yield break;
        }

        var day = timeProvider.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var yielded = 0;
        foreach (var chart in config.Charts)
        {
            FeedEnvelope? envelope;
            try
            {
                var url = $"{config.BaseUrl}/{config.Country}/apps/{chart}/{config.PerChart}/apps.json";
                envelope = await httpClient.GetFromJsonAsync<FeedEnvelope>(new Uri(url), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "App Store chart {Chart} fetch failed", chart);
                continue;
            }

            var results = envelope?.Feed?.Results ?? [];
            for (var rank = 0; rank < results.Count; rank++)
            {
                if (yielded >= options.MaxItems)
                {
                    yield break;
                }

                var app = results[rank];
                if (app.Name is not { Length: > 0 })
                {
                    continue;
                }

                yielded++;
                yield return new RawItem
                {
                    Source = SourceKind.AppStoreCharts,
                    ExternalId = $"{chart}:{app.Id}:{day}",
                    Title = $"App Store {chart} #{rank + 1}: {app.Name}",
                    Body = $"by {app.ArtistName} · genres: {string.Join(", ", (app.Genres ?? []).Select(g => g.Name))}",
                    Url = app.Url,
                    Author = app.ArtistName,
                    Community = app.Genres is { Count: > 0 } genres ? genres[0].Name ?? chart : chart,
                    Score = config.PerChart - rank, // rank inverted: #1 = strongest signal
                    CreatedAt = timeProvider.GetUtcNow(),
                    FetchedAt = timeProvider.GetUtcNow(),
                };
            }
        }
    }

    private sealed record FeedEnvelope([property: JsonPropertyName("feed")] FeedBody? Feed);

    private sealed record FeedBody([property: JsonPropertyName("results")] IReadOnlyList<AppRow>? Results);

    private sealed record AppRow(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("artistName")] string? ArtistName,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("genres")] IReadOnlyList<GenreRow>? Genres);

    private sealed record GenreRow([property: JsonPropertyName("name")] string? Name);
}
