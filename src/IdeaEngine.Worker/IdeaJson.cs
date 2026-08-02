using System.Text.Json;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ideation;
using IdeaEngine.Infrastructure.Persistence.Entities;

namespace IdeaEngine.Worker;

/// <summary>Shared helpers for reading our own idea jsonb columns (commands + autopilot).</summary>
internal static class IdeaJson
{
    public static double ComputeRating(IdeaEntity idea)
    {
        var scores = SafeDeserialize<Dictionary<string, double>>(idea.ScoresJson);
        var skeptic = SafeDeserialize<SkepticReview>(idea.SkepticJson);
        return IdeaScoring.Rating(scores, skeptic?.Confidence ?? 0);
    }

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
