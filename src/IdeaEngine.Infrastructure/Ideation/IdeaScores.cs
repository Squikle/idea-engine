using IdeaEngine.Core.Pipeline;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Persistence.Entities;
using IdeaEngine.Infrastructure.Research;

namespace IdeaEngine.Infrastructure.Ideation;

/// <summary>Unified-score helpers over persisted jsonb (shared by Worker UI and sweeps).</summary>
public static class IdeaScores
{
    /// <summary>THE score: research-backed when a report exists, skeptic estimate otherwise.</summary>
    public static IdeaScore Compute(IdeaEntity idea, ResearchReportDto? research) =>
        IdeaScoring.Compute(
            LlmJson.SafeDeserialize<Dictionary<string, double>>(idea.ScoresJson),
            LlmJson.SafeDeserialize<SkepticReview>(idea.SkepticJson)?.Confidence ?? 0,
            research?.Scores,
            research?.Confidence ?? 0);

    public static double Rating(IdeaEntity idea) => Compute(idea, null).Total;
}
