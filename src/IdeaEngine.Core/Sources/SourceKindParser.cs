namespace IdeaEngine.Core.Sources;

/// <summary>Human-friendly source name parsing for bot commands (/collect hn).</summary>
public static class SourceKindParser
{
    public static bool TryParse(string? input, out SourceKind kind)
    {
        kind = SourceKind.Unknown;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        kind = input.Trim().ToLowerInvariant() switch
        {
            "hn" or "hackernews" or "hacker_news" => SourceKind.HackerNews,
            "4chan" or "fourchan" or "chan" => SourceKind.FourChan,
            "bsky" or "bluesky" => SourceKind.Bluesky,
            "lemmy" => SourceKind.Lemmy,
            "reddit" or "rss" or "redditrss" => SourceKind.RedditRss,
            "youtube" or "yt" => SourceKind.YouTube,
            "gdelt" or "news" => SourceKind.Gdelt,
            _ => SourceKind.Unknown,
        };

        return kind != SourceKind.Unknown;
    }
}
