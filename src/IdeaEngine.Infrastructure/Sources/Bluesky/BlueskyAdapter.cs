using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IdeaEngine.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Sources.Bluesky;

/// <summary>
/// Mines Bluesky search with an app-password session (search requires auth since 2026).
/// A fresh short-lived session is created per fetch run; the post itself is the signal,
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
        if (!config.IsConfigured)
        {
            logger.LogInformation("Bluesky skipped: BLUESKY_IDENTIFIER/APP_PASSWORD not set");
            yield break;
        }

        var accessJwt = await CreateSessionAsync(config, cancellationToken);
        if (accessJwt is null)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var yielded = 0;

        foreach (var query in config.Queries)
        {
            if (yielded >= options.MaxItems)
            {
                yield break;
            }

            await Task.Delay(config.PolitenessDelayMs, cancellationToken);
            var posts = await SearchAsync(query, config.LimitPerQuery, accessJwt, cancellationToken);

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

    private async Task<string?> CreateSessionAsync(BlueskyOptions config, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                new Uri("xrpc/com.atproto.server.createSession", UriKind.Relative),
                new { identifier = config.Identifier, password = config.AppPassword },
                JsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();

            var session = await response.Content.ReadFromJsonAsync<BlueskySessionResponse>(
                JsonOptions, cancellationToken);
            if (string.IsNullOrWhiteSpace(session?.AccessJwt))
            {
                logger.LogWarning("Bluesky session response had no access token");
                return null;
            }

            return session.AccessJwt;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Bluesky session creation failed (bad app password?)");
            return null;
        }
    }

    private async Task<IReadOnlyList<BlueskyPost>> SearchAsync(
        string query, int limit, string accessJwt, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"xrpc/app.bsky.feed.searchPosts?q={Uri.EscapeDataString($"\"{query}\"")}&sort=top&limit={limit}";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessJwt);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var parsed = await response.Content.ReadFromJsonAsync<BlueskySearchResponse>(
                JsonOptions, cancellationToken);
            return parsed?.Posts ?? [];
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
