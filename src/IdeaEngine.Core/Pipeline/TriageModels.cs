using IdeaEngine.Core.Sources;

namespace IdeaEngine.Core.Pipeline;

/// <summary>Condensed item content sent to the triage model.</summary>
public sealed record TriageInput(
    long ItemId,
    SourceKind Source,
    string? Community,
    string Title,
    string? Body,
    long Score,
    int CommentCount,
    IReadOnlyList<RawComment> Comments);

/// <summary>One extracted product-opportunity signal.</summary>
public sealed record SignalDraft(
    string Kind,
    string Summary,
    string? Audience,
    string CommercialSentiment,
    double Novelty,
    double Confidence);

/// <summary>Model verdict for one item. Empty signals is the expected common case.</summary>
public sealed record TriageVerdict(
    double Relevance,
    string Language,
    IReadOnlyList<SignalDraft> Signals);

/// <summary>Verdict plus accounting.</summary>
public sealed record TriageOutcome(TriageVerdict? Verdict, long TokensIn, long TokensOut);

/// <summary>Analyzes one item for product-opportunity signals. Implementations must never throw.</summary>
public interface ITriageAnalyzer
{
    bool IsConfigured { get; }

    Task<TriageOutcome> AnalyzeAsync(TriageInput input, CancellationToken cancellationToken);
}
