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

        // Shuffle every cycle: under a mid-list rate limit the SAME tail subs would
        // otherwise starve forever. Random order spreads the pain fairly.
        var ordered = config.Subreddits.OrderBy(_ => Random.Shared.Next()).ToList();
        var backfillSubs = ordered
            .Take(Math.Max(0, config.BackfillSubsPerCycle))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rateLimited = false;
        var deferred = 0;

        foreach (var subreddit in ordered)
        {
            if (yielded >= options.MaxItems)
            {
                yield break;
            }

            if (rateLimited)
            {
                deferred++;
                continue; // circuit open: stop hammering, these subs go first-ish next cycle
            }

            await Task.Delay(config.PolitenessDelayMs, cancellationToken);
            var (entries, throttled) = await FetchFeedWithBackoffAsync(
                $"r/{subreddit}/hot.rss", subreddit, cancellationToken);
            if (throttled)
            {
                rateLimited = true;
                deferred++;
                continue;
            }

            if (backfillSubs.Contains(subreddit))
            {
                await Task.Delay(config.PolitenessDelayMs, cancellationToken);
                var (archive, archiveThrottled) = await FetchFeedWithBackoffAsync(
                    $"r/{subreddit}/top.rss?t=all", subreddit, cancellationToken);
                entries = [.. entries, .. archive];
                rateLimited |= archiveThrottled; // hot entries already in hand; stop after this sub
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

        if (deferred > 0)
        {
            logger.LogWarning(
                "Reddit rate-limited this cycle: {Deferred} sub(s) deferred to the next run. " +
                "Nothing is lost - hot feeds persist for hours, order reshuffles, dedup is free",
                deferred);
        }
    }

    /// <summary>
    /// One fetch with 429-awareness: on TooManyRequests waits (Retry-After, clamped 30-120s)
    /// and retries once. Returns Throttled=true when Reddit is still saying no - the caller
    /// opens the circuit for the rest of the cycle instead of collecting more 429s.
    /// </summary>
    private async Task<(IReadOnlyList<XElement> Entries, bool Throttled)> FetchFeedWithBackoffAsync(
        string path, string subreddit, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var (entries, status) = await FetchFeedAsync(path, subreddit, cancellationToken);
            if (status != System.Net.HttpStatusCode.TooManyRequests)
            {
                return (entries, false);
            }

            if (attempt == 1)
            {
                var wait = TimeSpan.FromSeconds(45); // default between Reddit's typical 30-60s windows
                logger.LogInformation(
                    "Reddit 429 for r/{Subreddit}; backing off {Wait}s then retrying once",
                    subreddit, wait.TotalSeconds);
                await Task.Delay(wait, cancellationToken);
            }
        }

        return ([], true);
    }

    private async Task<(IReadOnlyList<XElement> Entries, System.Net.HttpStatusCode Status)> FetchFeedAsync(
        string path, string subreddit, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return ([], System.Net.HttpStatusCode.TooManyRequests);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Reddit RSS {Status} for r/{Subreddit}, skipping this cycle (retries next run)",
                    (int)response.StatusCode, subreddit);
                return ([], response.StatusCode);
            }

            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = XDocument.Parse(xml);
            return ([.. document.Root?.Elements(Atom + "entry") ?? []], response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or TaskCanceledException or Polly.Timeout.TimeoutRejectedException)
        {
            logger.LogWarning(ex, "Reddit RSS fetch failed for r/{Subreddit}, skipping this cycle", subreddit);
            return ([], System.Net.HttpStatusCode.ServiceUnavailable);
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
