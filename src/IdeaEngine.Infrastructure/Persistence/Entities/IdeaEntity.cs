namespace IdeaEngine.Infrastructure.Persistence.Entities;

/// <summary>
/// A synthesized opportunity (or a meta-proposal about the pipeline itself,
/// Category = "meta"). Dismissed ideas are kept forever for retrospectives.
/// </summary>
public sealed class IdeaEntity
{
    public long Id { get; set; }

    public required string Title { get; set; }

    public required string Thesis { get; set; }

    /// <summary>saas|app|website|3dprint|hardware|wearable|service|content|meta.</summary>
    public required string Category { get; set; }

    /// <summary>1=weekend project … 5=real business build.</summary>
    public int EffortScale { get; set; }

    public string? TargetUser { get; set; }

    public string? Monetization { get; set; }

    public string? DistributionNote { get; set; }

    /// <summary>candidate | validated | hot | dismissed.</summary>
    public required string Status { get; set; }

    /// <summary>ai | operator (dropped via /drop).</summary>
    public string Origin { get; set; } = "ai";

    /// <summary>Adjacent applications of the same mechanic, jsonb array.</summary>
    public string? VariantsJson { get; set; }

    /// <summary>Signal ids the builder cited, jsonb array.</summary>
    public string? EvidenceJson { get; set; }

    /// <summary>Skeptic scores (demand/wtp/feasibility/differentiation), jsonb.</summary>
    public string? ScoresJson { get; set; }

    /// <summary>Full skeptic review (verdict, reasons, research questions), jsonb.</summary>
    public string? SkepticJson { get; set; }

    public string? BuilderModel { get; set; }

    public string? SkepticModel { get; set; }

    public decimal CostUsd { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
