using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IdeaEngine.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Sources.Bluesky;

/// <summary>
/// Mines Bluesky via the public AppView (no auth required for public reads).
/// Strategy: keyword-search literal pain-point phrases; the post itself is the signal,
/// so replies are not fetched (reply/like counts still captured for scoring).
/// </summary>
public sealed class BlueskyAdapter(
    HttpClient httpClient,
    TimeProvider timeProvider,
    IOptions<BlueskyOptions> adapterOptions,
    ILogger<BlueskyAdapter> logger) : ISourceAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SourceKind Kind => SourceKind.Bluesky;

    public async IAsyncEnumerable<RawItem> FetchAsync(
        SourceFetchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = adapterOptions.Value;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var yielded = 0;

        foreach (var query in config.Queries)
        {
            if (yielded >= options.MaxItems)
            {
                yield break;
            }

            await Task.Delay(config.PolitenessDelayMs, cancellationToken);
            var posts = await SearchAsync(query, config.LimitPerQuery, cancellationToken);

            foreach (var post in posts)
            {
                if (yielded >= options.MaxItems)
                {
                    yield break;
                }

                var item = Map(post, query, config, options.Since);
                if (item is null || !seen.Add(item.ExternalId))
                {
                    continue;
                }

                yielded++;
                yield return item;
            }
        }
    }

    private async Task<IReadOnlyList<BlueskyPost>> SearchAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"xrpc/app.bsky.feed.searchPosts?q={Uri.EscapeDataString($"\"{query}\"")}&sort=top&limit={limit}";
            var response = await httpClient.GetFromJsonAsync<BlueskySearchResponse>(
                new Uri(url, UriKind.Relative), JsonOptions, cancellationToken);
            return response?.Posts ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Bluesky search failed for query '{Query}', continuing", query);
            return [];
        }
    }

    private RawItem? Map(BlueskyPost post, string query, BlueskyOptions config, DateTimeOffset? since)
    {
        var text = post.Record?.Text?.Trim();
        if (post.Uri is null || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var likes = post.LikeCount ?? 0;
        if (likes < config.MinLikes)
        {
            return null;
        }

        var createdAt = post.Record?.CreatedAt ?? timeProvider.GetUtcNow();
        if (since is { } cutoff && createdAt < cutoff)
        {
            return null;
        }

        return new RawItem
        {
            Source = SourceKind.Bluesky,
            ExternalId = post.Uri,
            Title = text.Length <= 120 ? text : text[..119] + "…",
            Body = text.Length > 120 ? text : null,
            Url = BuildWebUrl(post),
            Author = post.Author?.Handle,
            Community = $"q:{query}",
            Score = likes + 2L * (post.RepostCount ?? 0),
            CommentCount = post.ReplyCount ?? 0,
            CreatedAt = createdAt,
            FetchedAt = timeProvider.GetUtcNow(),
        };
    }

    /// <summary>at://did:plc:xyz/app.bsky.feed.post/RKEY → https://bsky.app/profile/HANDLE/post/RKEY</summary>
    private static string? BuildWebUrl(BlueskyPost post)
    {
        var handle = post.Author?.Handle;
        var rkey = post.Uri?.Split('/') is { Length: > 0 } parts ? parts[^1] : null;
        return handle is null || rkey is null ? null : $"https://bsky.app/profile/{handle}/post/{rkey}";
    }
}
