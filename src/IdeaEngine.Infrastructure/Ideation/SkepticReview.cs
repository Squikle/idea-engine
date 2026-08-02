using System.Text.Json.Serialization;

namespace IdeaEngine.Infrastructure.Ideation;

/// <summary>
/// Skeptic verdict shape - written by IdeationService, read back for /idea trace cards.
/// Property names match the prompt contract in IdeationPrompts.SkepticSystem.
/// </summary>
public sealed record SkepticReview(
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("kill_reasons")] IReadOnlyList<string>? KillReasons,
    [property: JsonPropertyName("weaknesses")] IReadOnlyList<string>? Weaknesses,
    [property: JsonPropertyName("existing_solutions")] IReadOnlyList<string>? ExistingSolutions,
    [property: JsonPropertyName("research_questions")] IReadOnlyList<string>? ResearchQuestions,
    [property: JsonPropertyName("scores")] Dictionary<string, double>? Scores,
    [property: JsonPropertyName("confidence")] double Confidence);
