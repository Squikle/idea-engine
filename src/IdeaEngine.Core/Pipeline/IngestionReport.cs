using IdeaEngine.Core.Sources;

namespace IdeaEngine.Core.Pipeline;

/// <summary>A stored item worth surfacing in the cycle notification.</summary>
public sealed record IngestedHighlight(string Title, string? Url, long Score, int CommentCount);

/// <summary>Outcome of ingesting one source within a cycle.</summary>
public sealed record SourceIngestResult(
    SourceKind Source,
    int Fetched,
    int Stored,
    int Duplicates,
    int Errors,
    double ElapsedSeconds,
    IReadOnlyList<IngestedHighlight> TopNew);

/// <summary>Outcome of a full ingestion cycle across all sources.</summary>
public sealed record IngestionCycleReport(
    DateTimeOffset FinishedAt,
    IReadOnlyList<SourceIngestResult> Sources)
{
    public int TotalStored => Sources.Sum(s => s.Stored);

    public int TotalErrors => Sources.Sum(s => s.Errors);
}
