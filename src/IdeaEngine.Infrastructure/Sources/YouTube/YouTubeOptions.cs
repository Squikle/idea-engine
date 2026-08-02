namespace IdeaEngine.Infrastructure.Sources.YouTube;

/// <summary>Bound from configuration section <c>IdeaEngine:Sources:YouTube</c>.</summary>
public sealed class YouTubeOptions
{
    /// <summary>Filled from env (YOUTUBE_API_KEY). Free quota: 10,000 units/day.</summary>
    public string? ApiKey { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Trending charts to read (1 quota unit per region per run).</summary>
    public IList<string> Regions { get; } = ["US", "CA"];

    /// <summary>Videos taken per region chart (API max 50).</summary>
    public int VideosPerRegion { get; set; } = 20;

    /// <summary>Top comments stored per video (1 quota unit per video).</summary>
    public int CommentsPerVideo { get; set; } = 10;

    /// <summary>Delay between API calls.</summary>
    public int PolitenessDelayMs { get; set; } = 250;

    /// <summary>Complaint-shaped Shorts searches (100 quota units each - the expensive call).</summary>
    public IList<string> MiningQueries { get; } =
    [
        "why is there no app for",
        "i wish there was an app",
        "so annoying that",
        "someone should invent",
    ];

    /// <summary>Shorts taken per mining query.</summary>
    public int MiningPerQuery { get; set; } = 4;

    /// <summary>Only Shorts published within this window.</summary>
    public int MiningPublishedDays { get; set; } = 7;

    /// <summary>Mining runs only in the cycle landing inside [hour, hour+3) UTC — once a day,
    /// keeping search-quota burn at ~450/10000 units instead of 8x that.</summary>
    public int MiningWindowUtcHour { get; set; } = 12;
}
