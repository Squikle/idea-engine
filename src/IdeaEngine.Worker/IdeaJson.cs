using System.Text.Json;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ideation;
using IdeaEngine.Infrastructure.Persistence.Entities;
using IdeaEngine.Infrastructure.Research;

namespace IdeaEngine.Worker;

/// <summary>Shared helpers for reading our own idea jsonb columns (commands + autopilot).</summary>
internal static class IdeaJson
{
    /// <summary>THE score: research-backed when a report exists, skeptic estimate otherwise.</summary>
    public static IdeaScore ComputeScore(IdeaEntity idea, ResearchReportDto? research) =>
        IdeaScores.Compute(idea, research);

    public static double ComputeRating(IdeaEntity idea) => IdeaScores.Rating(idea);

    public static List<long> ParseEvidence(string? evidenceJson) =>
        SafeDeserialize<List<long>>(evidenceJson) ?? [];

    /// <summary>For OUR OWN serialized jsonb columns (not LLM output - that's LlmJson's job).</summary>
    public static T? SafeDeserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, LlmJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
