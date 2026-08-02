namespace IdeaEngine.Infrastructure.Sources.HackerNews;

/// <summary>Bound from configuration section <c>IdeaEngine:Sources:HackerNews</c>.</summary>
public sealed class HackerNewsOptions
{
    /// <summary>Stories to take from the current front page per run.</summary>
    public int FrontPageLimit { get; set; } = 30;

    /// <summary>"Ask HN" posts to take per run (rich in pain points / requests).</summary>
    public int AskHnLimit { get; set; } = 25;

    /// <summary>Minimum points for Ask HN posts (front page is already curated).</summary>
    public int MinAskPoints { get; set; } = 5;

    /// <summary>Top comments fetched per story.</summary>
    public int CommentsPerItem { get; set; } = 12;
}
