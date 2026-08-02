namespace IdeaEngine.Infrastructure.Sources.Bluesky;

/// <summary>Bound from configuration section <c>IdeaEngine:Sources:Bluesky</c>.</summary>
public sealed class BlueskyOptions
{
    /// <summary>
    /// Search queries mined per run. Defaults are literal pain-point phrases -
    /// people telling the internet exactly what product is missing.
    /// </summary>
    public IList<string> Queries { get; } =
    [
        "wish there was an app",
        "someone should make",
        "why is there no",
        "i would pay for",
        "does anyone make",
    ];

    /// <summary>Posts requested per query (API max 100).</summary>
    public int LimitPerQuery { get; set; } = 25;

    /// <summary>Posts with fewer likes are ignored (noise floor).</summary>
    public int MinLikes { get; set; } = 3;

    /// <summary>Delay between search calls.</summary>
    public int PolitenessDelayMs { get; set; } = 400;
}
