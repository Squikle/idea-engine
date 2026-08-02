namespace IdeaEngine.Core.Sources;

/// <summary>
/// Every source the pipeline knows about, including planned ones.
/// Values are stored in the database - never renumber existing members.
/// </summary>
public enum SourceKind
{
    Unknown = 0,

    // Tier 1 (MVP)
    Reddit = 1,
    HackerNews = 2,
    FourChan = 3,

    // Tier 2 (Phase 3)
    Gdelt = 4,
    YouTube = 5,
    ProductHunt = 6,
    Etsy = 7,
    Ebay = 8,
    GoogleTrends = 9,
    Arxiv = 10,
}
