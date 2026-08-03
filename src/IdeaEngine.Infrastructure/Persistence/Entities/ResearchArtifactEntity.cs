namespace IdeaEngine.Infrastructure.Persistence.Entities;

/// <summary>
/// Research scaffolding kept for retrospection: SERPs, page excerpts, the advocate's
/// case, and raw synthesis output on parse failure. Cheap text that lets us re-mine
/// niches, combine idea strengths and audit judgments without re-paying for AI.
/// Kinds: serp | page | advocate | synthesis_raw.
/// </summary>
public sealed class ResearchArtifactEntity
{
    public long Id { get; set; }

    public long IdeaId { get; set; }

    /// <summary>Null when the run produced no report row (e.g. synthesis parse failure).</summary>
    public long? ReportId { get; set; }

    public required string Kind { get; set; }

    /// <summary>Order within (report, kind) — SERP #2, page #3, …</summary>
    public int Seq { get; set; }

    /// <summary>Always valid JSON (raw model text is wrapped as {"raw": …}).</summary>
    public required string Json { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
