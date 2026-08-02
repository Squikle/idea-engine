namespace IdeaEngine.Core.Sources;

/// <summary>Bounds for a single fetch run of one source.</summary>
public sealed record SourceFetchOptions
{
    /// <summary>Upper bound of items to yield per run (post-pagination, pre-filter).</summary>
    public int MaxItems { get; init; } = 100;

    /// <summary>If set, adapters should skip items created before this instant.</summary>
    public DateTimeOffset? Since { get; init; }
}
