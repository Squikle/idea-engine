namespace IdeaEngine.Infrastructure.Sources.Lemmy;

/// <summary>Bound from configuration section <c>IdeaEngine:Sources:Lemmy</c>.</summary>
public sealed class LemmyOptions
{
    /// <summary>Instance to read (any public Lemmy instance works).</summary>
    public string BaseUrl { get; set; } = "https://lemmy.world/";

    /// <summary>Posts requested from the top-of-day listing (API max 50).</summary>
    public int ListLimit { get; set; } = 40;

    /// <summary>Posts below this score are ignored.</summary>
    public int MinScore { get; set; } = 15;

    /// <summary>Best posts kept per run (comments are fetched only for these).</summary>
    public int TakeTop { get; set; } = 20;

    /// <summary>Comments stored per post.</summary>
    public int CommentsPerPost { get; set; } = 12;

    /// <summary>Delay between API calls.</summary>
    public int PolitenessDelayMs { get; set; } = 400;

    /// <summary>All-time top posts mined per run (archaeology; dedup makes repeats free).</summary>
    public int BackfillPerRun { get; set; } = 12;

    /// <summary>Score floor for all-time backfill posts.</summary>
    public int MinBackfillScore { get; set; } = 300;
}
