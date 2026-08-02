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
}
