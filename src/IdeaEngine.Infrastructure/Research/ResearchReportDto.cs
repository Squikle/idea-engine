using System.Text.Json.Serialization;

namespace IdeaEngine.Infrastructure.Research;

/// <summary>
/// The research report shape - written by ResearchService, read back by /idea cards.
/// Property names match the prompt contract in ResearchPrompts.SynthesisSystem.
/// </summary>
public sealed record ResearchReportDto(
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("competitors")] IReadOnlyList<CompetitorDto>? Competitors,
    [property: JsonPropertyName("answers")] IReadOnlyList<AnswerDto>? Answers,
    [property: JsonPropertyName("market_notes")] string? MarketNotes,
    [property: JsonPropertyName("differentiation_path")] string? DifferentiationPath,
    [property: JsonPropertyName("risks")] IReadOnlyList<string>? Risks,
    [property: JsonPropertyName("mvp_test")] string? MvpTest,
    [property: JsonPropertyName("related_variants")] IReadOnlyList<string>? RelatedVariants,
    [property: JsonPropertyName("next_steps")] IReadOnlyList<string>? NextSteps,
    [property: JsonPropertyName("scores")] Dictionary<string, double>? Scores,
    [property: JsonPropertyName("concerns")] IReadOnlyList<ConcernDto>? Concerns);

/// <summary>
/// One entry of the concern ledger - the distillation unit. Concerns are carried across
/// research rounds by name and must be re-adjudicated every round: closed, hardened or kept.
/// </summary>
public sealed record ConcernDto(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("mitigation")] string? Mitigation,
    [property: JsonPropertyName("resolved_by")] string? ResolvedBy);

public sealed record CompetitorDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("why")] string? Why);

public sealed record AnswerDto(
    [property: JsonPropertyName("question")] string? Question,
    [property: JsonPropertyName("answer")] string? Answer,
    [property: JsonPropertyName("evidence_urls")] IReadOnlyList<string>? EvidenceUrls)
{
    /// <summary>True when the answer is genuinely grounded, not a "not found" placeholder.</summary>
    [JsonIgnore]
    public bool IsAnswered =>
        !string.IsNullOrWhiteSpace(Answer)
        && !Answer.Contains("not found", StringComparison.OrdinalIgnoreCase)
        && !Answer.Contains("no results", StringComparison.OrdinalIgnoreCase);
}
