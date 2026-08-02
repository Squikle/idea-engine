namespace IdeaEngine.Infrastructure.Persistence.Entities;

/// <summary>One execution of one pipeline stage; feeds /status and the digest health footer.</summary>
public sealed class PipelineRunEntity
{
    public long Id { get; set; }

    /// <summary>Stage name, e.g. "ingest:hackernews", "prefilter", "triage".</summary>
    public required string Stage { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public int ItemsIn { get; set; }

    public int ItemsOut { get; set; }

    public int Errors { get; set; }

    public decimal CostUsd { get; set; }

    public string? Notes { get; set; }
}
