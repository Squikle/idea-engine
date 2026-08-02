using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Sources.FourChan;

/// <summary>
/// Reads 4chan via the official read-only JSON API (https://github.com/4chan/4chan-API).
/// High-noise source: only well-replied threads from configured boards survive, and the
/// prefilter + triage stages do the rest. Respects the 1 request/second API rule.
/// </summary>
public sealed class FourChanAdapter(
    HttpClient httpClient,
    TimeProvider timeProvider,
    IOptions<FourChanOptions> adapterOptions,
    ILogger<FourChanAdapter> logger) : ISourceAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SourceKind Kind => SourceKind.FourChan;

    public async IAsyncEnumerable<RawItem> FetchAsync(
        SourceFetchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = adapterOptions.Value;
        var yielded = 0;

        foreach (var board in config.Boards)
        {
            if (yielded >= options.MaxItems)
            {
                yield break;
            }

            var candidates = await FetchCatalogCandidatesAsync(board, config, options.Since, cancellationToken);

            foreach (var thread in candidates)
            {
                if (yielded >= options.MaxItems)
                {
                    yield break;
                }

                await Task.Delay(config.PolitenessDelayMs, cancellationToken);
                var item = await FetchThreadAsync(board, thread, config, cancellationToken);
                if (item is null)
                {
                    continue;
                }

                yielded++;
                yield return item;
            }
        }
    }

    private async Task<IReadOnlyList<FourChanPost>> FetchCatalogCandidatesAsync(
        string board, FourChanOptions config, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(config.PolitenessDelayMs, cancellationToken);
            var pages = await httpClient.GetFromJsonAsync<IReadOnlyList<FourChanCatalogPage>>(
                new Uri($"{board}/catalog.json", UriKind.Relative), JsonOptions, cancellationToken);

            var sinceUnix = since?.ToUnixTimeSeconds();

            return
            [
                .. (pages ?? [])
                    .SelectMany(p => p.Threads ?? [])
                    .Where(t => t.Sticky is not 1)
                    .Where(t => (t.Replies ?? 0) >= config.MinReplies)
                    .Where(t => sinceUnix is null || (t.LastModified ?? t.Time) >= sinceUnix)
                    .OrderByDescending(t => t.Replies ?? 0)
                    .Take(config.ThreadsPerBoard),
            ];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "4chan catalog fetch failed for /{Board}/, skipping board", board);
            return [];
        }
    }

    private async Task<RawItem?> FetchThreadAsync(
        string board, FourChanPost thread, FourChanOptions config, CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<FourChanThreadResponse>(
                new Uri($"{board}/thread/{thread.No}.json", UriKind.Relative), JsonOptions, cancellationToken);

            var posts = response?.Posts;
            if (posts is null || posts.Count == 0)
            {
                return null;
            }

            var op = posts[0];
            var body = HtmlText.ToPlainText(op.CommentHtml);
            var title = !string.IsNullOrWhiteSpace(op.Subject)
                ? HtmlText.ToPlainText(op.Subject)
                : Truncate(body, 120);

            if (string.IsNullOrWhiteSpace(title))
            {
                return null; // image-only thread with no text at all
            }

            var comments = posts
                .Skip(1)
                .Select(p => new RawComment(p.Name, HtmlText.ToPlainText(p.CommentHtml), 0))
                .Where(c => c.Text.Length >= 15)
                .Take(config.CommentsPerThread)
                .ToList();

            return new RawItem
            {
                Source = SourceKind.FourChan,
                ExternalId = $"{board}/{thread.No}",
                Title = title,
                Body = string.IsNullOrWhiteSpace(body) ? null : body,
                Url = $"https://boards.4chan.org/{board}/thread/{thread.No}",
                Author = op.Name,
                Community = board,
                Score = thread.Replies ?? 0,
                CommentCount = thread.Replies ?? 0,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(op.Time),
                FetchedAt = timeProvider.GetUtcNow(),
                Comments = comments,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "4chan thread fetch failed: /{Board}/{No}", board, thread.No);
            return null;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
