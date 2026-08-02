namespace IdeaEngine.Infrastructure.Sources.RedditRss;

/// <summary>Bound from configuration section <c>IdeaEngine:Sources:RedditRss</c>.</summary>
public sealed class RedditRssOptions
{
    /// <summary>
    /// Subreddits read via public Atom feeds (interim until the Data API request is
    /// approved - see ADR-0004). Deliberately wide: hobby niches carry the ideas.
    /// </summary>
    public IList<string> Subreddits { get; } =
    [
        "3Dprinting", "functionalprint", "somethingimade", "DIY", "cosplayers",
        "EDC", "MechanicalKeyboards", "fpv", "aquariums", "HomeAutomation",
        "gadgets", "SomebodyMakeThis", "DidntKnowIWantedThat", "shutupandtakemymoney",
        "BuyItForLife", "mildlyinfuriating", "smallbusiness", "SideProject", "Entrepreneur",
        "dropship", "ecommerce",
    ];

    /// <summary>Top feed entries kept per subreddit.</summary>
    public int PerSubredditLimit { get; set; } = 10;

    /// <summary>Delay between feed requests; stay far below anything rate-limit-shaped.</summary>
    public int PolitenessDelayMs { get; set; } = 2100;

    /// <summary>Per cycle, this many randomly chosen subs ALSO get their all-time-top feed
    /// mined (archaeology - old high-vote threads; dedup keeps repeats free).</summary>
    public int BackfillSubsPerCycle { get; set; } = 3;
}
