using System.Runtime.CompilerServices;
using System.Xml.Linq;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Sources.RedditRss;

/// <summary>
/// Interim Reddit source via public Atom feeds (/r/{sub}/hot.rss) while Data API approval
/// is pending. Feeds expose no vote scores, so hot-feed position is used as the popularity
/// proxy: Score = PerSubredditLimit - position (first item highest). Comments are not
/// fetched (would double request volume); triage works from titles + selftext.
/// Replaced by the OAuth adapter under the same ISourceAdapter contract once approved.
/// </summary>
public sealed class RedditRssAdapter(
    HttpClient httpClient,
    TimeProvider timeProvider,
    IOptions<RedditRssOptions> adapterOptions,
    ILogger<RedditRssAdapter> logger) : ISourceAdapter
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    public SourceKind Kind => SourceKind.RedditRss;

    public async IAsyncEnumerable<RawItem> FetchAsync(
        SourceFetchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = adapterOptions.Value;
        var yielded = 0;

        var backfillSubs = config.Subreddits
            .OrderBy(_ => Random.Shared.Next())
            .Take(Math.Max(0, config.BackfillSubsPerCycle))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var subreddit in config.Subreddits)
        {
            if (yielded >= options.MaxItems)
            {
                yield break;
            }

            await Task.Delay(config.PolitenessDelayMs, cancellationToken);
            var entries = await FetchFeedAsync($"r/{subreddit}/hot.rss", subreddit, cancellationToken);
            if (backfillSubs.Contains(subreddit))
            {
                await Task.Delay(config.PolitenessDelayMs, cancellationToken);
                entries = [.. entries, .. await FetchFeedAsync($"r/{subreddit}/top.rss?t=all", subreddit, cancellationToken)];
            }

            var position = 0;
            foreach (var entry in entries.Take(config.PerSubredditLimit))
            {
                if (yielded >= options.MaxItems)
                {
                    yield break;
                }

                var item = MapEntry(entry, subreddit, position, config.PerSubredditLimit, options.Since);
                position++;

                if (item is null)
                {
                    continue;
                }

                yielded++;
                yield return item;
            }
        }
    }

    private async Task<IReadOnlyList<XElement>> FetchFeedAsync(
        string path, string subreddit, CancellationToken cancellationToken)
    {
        try
        {
            var xml = await httpClient.GetStringAsync(
                new Uri(path, UriKind.Relative), cancellationToken);
            var document = XDocument.Parse(xml);
            return [.. document.Root?.Elements(Atom + "entry") ?? []];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Reddit RSS fetch failed for r/{Subreddit}, skipping", subreddit);
            return [];
        }
    }

    private RawItem? MapEntry(
        XElement entry, string subreddit, int position, int perSubLimit, DateTimeOffset? since)
    {
        var externalId = entry.Element(Atom + "id")?.Value;
        var title = entry.Element(Atom + "title")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var link = entry.Element(Atom + "link")?.Attribute("href")?.Value;
        var author = entry.Element(Atom + "author")?.Element(Atom + "name")?.Value?.TrimStart('/');
        var published = DateTimeOffset.TryParse(
            entry.Element(Atom + "published")?.Value ?? entry.Element(Atom + "updated")?.Value,
            out var parsed)
            ? parsed
            : timeProvider.GetUtcNow();

        // No Since cutoff: archaeology deliberately surfaces old high-value threads;
        // dedup by external id keeps repeats free.
        _ = since;

        var body = HtmlText.ToPlainText(entry.Element(Atom + "content")?.Value);
        // Reddit feed content wraps everything in "submitted by ... [link] [comments]" chrome.
        if (body.Length < 40)
        {
            body = string.Empty;
        }

        return new RawItem
        {
            Source = SourceKind.RedditRss,
            ExternalId = externalId,
            Title = title,
            Body = body.Length > 0 ? body : null,
            Url = link,
            Author = author,
            Community = subreddit,
            Score = perSubLimit - position, // hot-feed position proxy; real votes need the Data API
            CommentCount = 0,
            CreatedAt = published,
            FetchedAt = timeProvider.GetUtcNow(),
        };
    }
}
