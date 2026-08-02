using IdeaEngine.Core.Pipeline;
using IdeaEngine.Core.Sources;
using Pgvector;

namespace IdeaEngine.Infrastructure.Persistence.Entities;

/// <summary>
/// Persisted form of a fetched item. Comments and the original payload are stored as
/// jsonb strings (serialized in the ingestion service) to keep the EF mapping boring.
/// </summary>
public sealed class RawItemEntity
{
    public long Id { get; set; }

    public SourceKind Source { get; set; }

    public required string ExternalId { get; set; }

    public required string Title { get; set; }

    public string? Body { get; set; }

    public string? Url { get; set; }

    public string? Author { get; set; }

    public string? Community { get; set; }

    public long Score { get; set; }

    public int CommentCount { get; set; }

    /// <summary>Cheap dedup hash of normalized title+body (see ContentHasher).</summary>
    public required string ContentHash { get; set; }

    /// <summary>JSON array of RawComment, jsonb column.</summary>
    public string? CommentsJson { get; set; }

    /// <summary>Original source payload for replay/reprocessing, jsonb column.</summary>
    public string? RawPayloadJson { get; set; }

    public ItemStatus Status { get; set; }

    /// <summary>384-dim MiniLM embedding; null until the embedding stage runs.</summary>
    public Vector? Embedding { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset FetchedAt { get; set; }
}
