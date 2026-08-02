using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Sources.HackerNews;

/// <summary>
/// Reads Hacker News via the public Algolia API (https://hn.algolia.com/api).
/// Two feeds per run: current front page + recent well-scored "Ask HN" posts.
/// No authentication required; generous public limits (10k req/h) vs our ~60 req/run.
/// </summary>
public sealed class HackerNewsAdapter(
    HttpClient httpClient,
    TimeProvider timeProvider,
    IOptions<HackerNewsOptions> adapterOptions,
    ILogger<HackerNewsAdapter> logger) : ISourceAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SourceKind Kind => SourceKind.HackerNews;

    public async IAsyncEnumerable<RawItem> FetchAsync(
        SourceFetchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = adapterOptions.Value;

        var frontPage = await SearchAsync(
            $"search?tags=front_page&hitsPerPage={config.FrontPageLimit}",
            cancellationToken);

        var askSince = (options.Since ?? timeProvider.GetUtcNow().AddDays(-2)).ToUnixTimeSeconds();
        var askHn = await SearchAsync(
            $"search_by_date?tags=ask_hn&hitsPerPage={config.AskHnLimit}" +
            $"&numericFilters=points>={config.MinAskPoints},created_at_i>={askSince}",
            cancellationToken);

        // Backfill archaeology: a random ~60-day window from the archive, top stories only.
        IReadOnlyList<AlgoliaHit> backfill = [];
        if (config.BackfillPerRun > 0)
        {
            var now = timeProvider.GetUtcNow();
            var windowStart = now.AddDays(-Random.Shared.Next(240, 365 * 12)); // 8 months .. 12 years back
            var windowEnd = windowStart.AddDays(60);
            backfill = await SearchAsync(
                $"search?tags=story&hitsPerPage={config.BackfillPerRun}" +
                $"&numericFilters=points>={config.MinBackfillPoints}," +
                $"created_at_i>={windowStart.ToUnixTimeSeconds()},created_at_i<={windowEnd.ToUnixTimeSeconds()}",
                cancellationToken);
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var yielded = 0;

        foreach (var hit in frontPage.Concat(askHn).Concat(backfill))
        {
            if (yielded >= options.MaxItems)
            {
                yield break;
            }

            if (hit.ObjectId is null || string.IsNullOrWhiteSpace(hit.Title) || !seenIds.Add(hit.ObjectId))
            {
                continue;
            }

            var createdAt = DateTimeOffset.FromUnixTimeSeconds(hit.CreatedAtI);
            var isBackfill = backfill.Contains(hit);
            if (!isBackfill && options.Since is { } since && createdAt < since)
            {
                continue;
            }

            var comments = await FetchTopCommentsAsync(hit.ObjectId, config.CommentsPerItem, cancellationToken);

            yielded++;
            yield return new RawItem
            {
                Source = SourceKind.HackerNews,
                ExternalId = hit.ObjectId,
                Title = hit.Title.Trim(),
                Body = NullIfEmpty(HtmlText.ToPlainText(hit.StoryText)),
                Url = hit.Url ?? $"https://news.ycombinator.com/item?id={hit.ObjectId}",
                Author = hit.Author,
                Community = isBackfill ? "archive" : hit.Tags?.Contains("ask_hn") == true ? "ask_hn" : "front_page",
                Score = hit.Points ?? 0,
                CommentCount = hit.NumComments ?? 0,
                CreatedAt = createdAt,
                FetchedAt = timeProvider.GetUtcNow(),
                Comments = comments,
            };
        }
    }

    private async Task<IReadOnlyList<AlgoliaHit>> SearchAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<AlgoliaSearchResponse>(
                new Uri(relativeUrl, UriKind.Relative), JsonOptions, cancellationToken);
            return response?.Hits ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "HN query failed, continuing without it: {Url}", relativeUrl);
            return [];
        }
    }

    private async Task<IReadOnlyList<RawComment>> FetchTopCommentsAsync(
        string storyId, int limit, CancellationToken cancellationToken)
    {
        var hits = await SearchAsync(
            $"search?tags=comment,story_{storyId}&hitsPerPage={limit}",
            cancellationToken);

        return
        [
            .. hits
                .Select(h => new RawComment(h.Author, HtmlText.ToPlainText(h.CommentText), h.Points ?? 0))
                .Where(c => !string.IsNullOrWhiteSpace(c.Text)),
        ];
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
