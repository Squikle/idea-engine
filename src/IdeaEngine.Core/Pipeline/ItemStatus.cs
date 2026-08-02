namespace IdeaEngine.Core.Pipeline;

/// <summary>
/// Lifecycle of a raw item moving through pipeline stages.
/// Stored in the database - never renumber existing members.
/// </summary>
public enum ItemStatus
{
    /// <summary>Ingested, not yet prefiltered.</summary>
    New = 0,

    /// <summary>Rejected by the heuristic prefilter (kept for stats, excluded from AI stages).</summary>
    FilteredOut = 1,

    /// <summary>Passed prefilter, waiting for LLM triage.</summary>
    PendingTriage = 2,

    /// <summary>Triage complete; signals (if any) extracted.</summary>
    Triaged = 3,

    /// <summary>Processing failed permanently after retries; excluded from stages, kept for diagnosis.</summary>
    Failed = 4,
}
