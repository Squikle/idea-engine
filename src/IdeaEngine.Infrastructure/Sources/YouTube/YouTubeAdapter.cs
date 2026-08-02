using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Sources.YouTube;

/// <summary>
/// Trending videos + their top comments via the official Data API v3 - the ToS-clean
/// substitute for "scroll reels and read the comments". ~50 quota units per run out of
/// the free 10,000/day. Score is stored as viewCount/1000 to keep cross-source numbers
/// in a comparable ballpark.
/// </summary>
public sealed class YouTubeAdapter(
    HttpClient httpClient,
    TimeProvider timeProvider,
    IOptions<YouTubeOptions> adapterOptions,
    ILogger<YouTubeAdapter> logger) : ISourceAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SourceKind Kind => SourceKind.YouTube;

    public async IAsyncEnumerable<RawItem> FetchAsync(
        SourceFetchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = adapterOptions.Value;
        if (!config.IsConfigured)
        {
            logger.LogInformation("YouTube skipped: YOUTUBE_API_KEY not set");
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var yielded = 0;

        // Shorts complaint-mining: expensive search calls run once a day (window gate).
        var hour = timeProvider.GetUtcNow().Hour;
        if (config.MiningQueries.Count > 0
            && hour >= config.MiningWindowUtcHour && hour < config.MiningWindowUtcHour + 3)
        {
            foreach (var query in config.MiningQueries)
            {
                if (yielded >= options.MaxItems)
                {
                    yield break;
                }

                await Task.Delay(config.PolitenessDelayMs, cancellationToken);
                var found = await SearchShortsAsync(query, config, cancellationToken);
                foreach (var item in found)
                {
                    if (yielded >= options.MaxItems)
                    {
                        yield break;
                    }

                    if (item.Id?.VideoId is not { Length: > 0 } videoId
                        || item.Snippet?.Title is not { Length: > 0 } shortTitle
                        || !seen.Add(videoId))
                    {
                        continue;
                    }

                    await Task.Delay(config.PolitenessDelayMs, cancellationToken);
                    var shortComments = await FetchCommentsAsync(videoId, config, cancellationToken);

                    yielded++;
                    yield return new RawItem
                    {
                        Source = SourceKind.YouTube,
                        ExternalId = videoId,
                        Title = shortTitle.Trim(),
                        Body = Truncate(HtmlText.ToPlainText(item.Snippet.Description), 1500),
                        Url = $"https://www.youtube.com/shorts/{videoId}",
                        Author = item.Snippet.ChannelTitle,
                        Community = $"shorts:{Slug(query)}",
                        Score = shortComments.Count, // search omits stats; comment density proxies traction
                        CommentCount = shortComments.Count,
                        CreatedAt = item.Snippet.PublishedAt ?? timeProvider.GetUtcNow(),
                        FetchedAt = timeProvider.GetUtcNow(),
                        Comments = shortComments,
                    };
                }
            }
        }

        foreach (var region in config.Regions)
        {
            if (yielded >= options.MaxItems)
            {
                yield break;
            }

            await Task.Delay(config.PolitenessDelayMs, cancellationToken);
            var videos = await FetchTrendingAsync(region, config, cancellationToken);

            foreach (var video in videos)
            {
                if (yielded >= options.MaxItems)
                {
                    yield break;
                }

                if (video.Id is null
                    || video.Snippet?.Title is not { Length: > 0 } title
                    || !seen.Add(video.Id))
                {
                    continue;
                }

                var createdAt = video.Snippet.PublishedAt ?? timeProvider.GetUtcNow();
                if (options.Since is { } since && createdAt < since)
                {
                    continue;
                }

                await Task.Delay(config.PolitenessDelayMs, cancellationToken);
                var comments = await FetchCommentsAsync(video.Id, config, cancellationToken);

                var views = ParseCount(video.Statistics?.ViewCount);
                yielded++;
                yield return new RawItem
                {
                    Source = SourceKind.YouTube,
                    ExternalId = video.Id,
                    Title = title.Trim(),
                    Body = Truncate(HtmlText.ToPlainText(video.Snippet.Description), 2000),
                    Url = $"https://www.youtube.com/watch?v={video.Id}",
                    Author = video.Snippet.ChannelTitle,
                    Community = $"trending:{region}",
                    Score = views / 1000, // thousands of views, cross-source comparable
                    CommentCount = (int)Math.Min(int.MaxValue, ParseCount(video.Statistics?.CommentCount)),
                    CreatedAt = createdAt,
                    FetchedAt = timeProvider.GetUtcNow(),
                    Comments = comments,
                };
            }
        }
    }

    private async Task<IReadOnlyList<YouTubeSearchItem>> SearchShortsAsync(
        string query, YouTubeOptions config, CancellationToken cancellationToken)
    {
        try
        {
            var publishedAfter = Uri.EscapeDataString(
                timeProvider.GetUtcNow().AddDays(-config.MiningPublishedDays)
                    .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            var url = "search?part=snippet&type=video&videoDuration=short&order=viewCount" +
                $"&publishedAfter={publishedAfter}&maxResults={config.MiningPerQuery}" +
                $"&q={Uri.EscapeDataString(query)}&key={config.ApiKey}";
            var response = await httpClient.GetFromJsonAsync<YouTubeSearchResponse>(
                new Uri(url, UriKind.Relative), JsonOptions, cancellationToken);
            return response?.Items ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "YouTube shorts search failed for '{Query}'", query);
            return [];
        }
    }

    private static string Slug(string query)
    {
        var clean = new string([.. query.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == ' ')])
            .Trim().Replace(' ', '-');
        return clean.Length > 24 ? clean[..24] : clean;
    }

    private async Task<IReadOnlyList<YouTubeVideo>> FetchTrendingAsync(
        string region, YouTubeOptions config, CancellationToken cancellationToken)
    {
        try
        {
            var url = "videos?part=snippet,statistics&chart=mostPopular" +
                $"&regionCode={region}&maxResults={config.VideosPerRegion}&key={config.ApiKey}";
            var response = await httpClient.GetFromJsonAsync<YouTubeVideosResponse>(
                new Uri(url, UriKind.Relative), JsonOptions, cancellationToken);
            return response?.Items ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "YouTube trending fetch failed for region {Region}", region);
            return [];
        }
    }

    private async Task<IReadOnlyList<RawComment>> FetchCommentsAsync(
        string videoId, YouTubeOptions config, CancellationToken cancellationToken)
    {
        try
        {
            var url = "commentThreads?part=snippet&order=relevance&textFormat=plainText" +
                $"&videoId={videoId}&maxResults={config.CommentsPerVideo}&key={config.ApiKey}";
            var response = await httpClient.GetFromJsonAsync<YouTubeCommentThreadsResponse>(
                new Uri(url, UriKind.Relative), JsonOptions, cancellationToken);

            return
            [
                .. (response?.Items ?? [])
                    .Select(t => t.Snippet?.TopLevelComment?.Snippet)
                    .Where(c => !string.IsNullOrWhiteSpace(c?.TextDisplay))
                    .Select(c => new RawComment(
                        c!.AuthorDisplayName,
                        Truncate(c.TextDisplay!.Trim(), 800)!,
                        c.LikeCount ?? 0)),
            ];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Comments disabled on the video is the common case here - not a problem.
            logger.LogDebug(ex, "YouTube comments unavailable for {VideoId}", videoId);
            return [];
        }
    }

    private static long ParseCount(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
