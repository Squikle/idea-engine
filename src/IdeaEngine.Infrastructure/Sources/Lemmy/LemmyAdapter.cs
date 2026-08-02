using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IdeaEngine.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Sources.Lemmy;

/// <summary>
/// Reads a public Lemmy instance (Reddit-shaped fediverse). No auth for public reads.
/// Top-of-day posts above a score floor; top comments fetched for kept posts only.
/// </summary>
public sealed class LemmyAdapter(
    HttpClient httpClient,
    TimeProvider timeProvider,
    IOptions<LemmyOptions> adapterOptions,
    ILogger<LemmyAdapter> logger) : ISourceAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SourceKind Kind => SourceKind.Lemmy;

    public async IAsyncEnumerable<RawItem> FetchAsync(
        SourceFetchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = adapterOptions.Value;

        var views = new List<LemmyPostView>(await FetchTopPostsAsync(config, cancellationToken));
        if (config.BackfillPerRun > 0)
        {
            // Random archive page of all-time top - old threads carry dense comment wisdom.
            var page = Random.Shared.Next(1, 20);
            views.AddRange(await FetchAllTimeTopAsync(config, page, cancellationToken));
        }

        var yielded = 0;

        foreach (var view in views)
        {
            if (yielded >= options.MaxItems)
            {
                yield break;
            }

            if (view.Post is not { } post || string.IsNullOrWhiteSpace(post.Name))
            {
                continue;
            }

            var createdAt = post.Published ?? timeProvider.GetUtcNow();
            var isBackfill = (view.Counts?.Score ?? 0) >= config.MinBackfillScore;
            if (!isBackfill && options.Since is { } since && createdAt < since)
            {
                continue;
            }

            await Task.Delay(config.PolitenessDelayMs, cancellationToken);
            var comments = await FetchCommentsAsync(post.Id, config, cancellationToken);

            yielded++;
            yield return new RawItem
            {
                Source = SourceKind.Lemmy,
                ExternalId = post.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Title = post.Name.Trim(),
                Body = string.IsNullOrWhiteSpace(post.Body) ? null : post.Body.Trim(),
                Url = post.Url ?? $"{config.BaseUrl.TrimEnd('/')}/post/{post.Id}",
                Author = view.Creator?.Name,
                Community = view.Community?.Name,
                Score = view.Counts?.Score ?? 0,
                CommentCount = view.Counts?.Comments ?? 0,
                CreatedAt = createdAt,
                FetchedAt = timeProvider.GetUtcNow(),
                Comments = comments,
            };
        }
    }

    private async Task<IReadOnlyList<LemmyPostView>> FetchAllTimeTopAsync(
        LemmyOptions config, int page, CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<LemmyPostListResponse>(
                new Uri($"api/v3/post/list?type_=All&sort=TopAll&limit={config.BackfillPerRun}&page={page}", UriKind.Relative),
                JsonOptions, cancellationToken);
            return
            [
                .. (response?.Posts ?? []).Where(v => (v.Counts?.Score ?? 0) >= config.MinBackfillScore),
            ];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Lemmy all-time backfill fetch failed (page {Page})", page);
            return [];
        }
    }

    private async Task<IReadOnlyList<LemmyPostView>> FetchTopPostsAsync(
        LemmyOptions config, CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<LemmyPostListResponse>(
                new Uri($"api/v3/post/list?type_=All&sort=TopDay&limit={config.ListLimit}", UriKind.Relative),
                JsonOptions, cancellationToken);

            return
            [
                .. (response?.Posts ?? [])
                    .Where(v => (v.Counts?.Score ?? 0) >= config.MinScore)
                    .OrderByDescending(v => v.Counts?.Score ?? 0)
                    .Take(config.TakeTop),
            ];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Lemmy post list fetch failed, skipping source this cycle");
            return [];
        }
    }

    private async Task<IReadOnlyList<RawComment>> FetchCommentsAsync(
        long postId, LemmyOptions config, CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<LemmyCommentListResponse>(
                new Uri($"api/v3/comment/list?post_id={postId}&sort=Top&limit={config.CommentsPerPost}", UriKind.Relative),
                JsonOptions, cancellationToken);

            return
            [
                .. (response?.Comments ?? [])
                    .Select(c => new RawComment(
                        c.Creator?.Name,
                        c.Comment?.Content?.Trim() ?? string.Empty,
                        c.Counts?.Score ?? 0))
                    .Where(c => c.Text.Length > 0),
            ];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Lemmy comments fetch failed for post {PostId}", postId);
            return [];
        }
    }
}
