namespace IdeaEngine.Core.Sources;

/// <summary>
/// A normalized item fetched from any source, before AI analysis.
/// This is the common shape every <see cref="ISourceAdapter"/> must produce.
/// </summary>
public sealed record RawItem
{
    public required SourceKind Source { get; init; }

    /// <summary>Stable id within the source (e.g. Reddit fullname, HN object id).</summary>
    public required string ExternalId { get; init; }

    public required string Title { get; init; }

    public string? Body { get; init; }

    public string? Url { get; init; }

    public string? Author { get; init; }

    /// <summary>Community/section within the source (subreddit, board, channel).</summary>
    public string? Community { get; init; }

    /// <summary>Source-native popularity score (upvotes, points).</summary>
    public long Score { get; init; }

    public int CommentCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset FetchedAt { get; init; }

    public IReadOnlyList<RawComment> Comments { get; init; } = [];
}
