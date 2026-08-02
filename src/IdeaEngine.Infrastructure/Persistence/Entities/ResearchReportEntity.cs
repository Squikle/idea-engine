namespace IdeaEngine.Infrastructure.Persistence.Entities;

/// <summary>Web-research validation report for one idea. Ideas can be re-researched later.</summary>
public sealed class ResearchReportEntity
{
    public long Id { get; set; }

    public long IdeaId { get; set; }

    public IdeaEntity? Idea { get; set; }

    /// <summary>go | maybe | no-go.</summary>
    public required string Verdict { get; set; }

    public double Confidence { get; set; }

    /// <summary>Full report (competitors, answers, risks, next steps, scores), jsonb.</summary>
    public required string ReportJson { get; set; }

    /// <summary>Search queries used, jsonb.</summary>
    public string? QueriesJson { get; set; }

    public int SearchesUsed { get; set; }

    public int SourcesCount { get; set; }

    /// <summary>App version whose reasoning produced this verdict (null = pre-0.22 era).</summary>
    public string? EngineVersion { get; set; }

    public required string Model { get; set; }

    public decimal CostUsd { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
