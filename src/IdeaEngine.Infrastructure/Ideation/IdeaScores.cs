using System.Text.Json.Serialization;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Persistence.Entities;
using IdeaEngine.Infrastructure.Research;

namespace IdeaEngine.Infrastructure.Ideation;

/// <summary>Unified-score helpers over persisted jsonb (shared by Worker UI and sweeps).</summary>
public static class IdeaScores
{
    /// <summary>
    /// THE score: research-backed when a report exists, skeptic estimate otherwise, with
    /// the court of appeal's category corrections applied when the appeal is NEWER than
    /// the report (older appeals were already digested by the following research run).
    /// </summary>
    public static IdeaScore Compute(
        IdeaEntity idea, ResearchReportDto? research, DateTimeOffset? reportAt = null)
    {
        var appeal = LlmJson.SafeDeserialize<AppealMeta>(idea.AppealJson);
        var adjustments = appeal?.ScoreAdjustments is { Count: > 0 } adj
            && (research is null || reportAt is null || appeal.At is null || appeal.At > reportAt)
                ? adj
                : null;

        return IdeaScoring.Compute(
            LlmJson.SafeDeserialize<Dictionary<string, double>>(idea.ScoresJson),
            LlmJson.SafeDeserialize<SkepticReview>(idea.SkepticJson)?.Confidence ?? 0,
            research?.Scores,
            research?.Confidence ?? 0,
            adjustments);
    }

    public static double Rating(IdeaEntity idea) => Compute(idea, null).Total;

    /// <summary>Timestamps needed for staleness: latest note and appeal moments.</summary>
    public static (DateTimeOffset? LastNoteAt, DateTimeOffset? AppealAt) JudgmentMoments(IdeaEntity idea)
    {
        var notes = LlmJson.SafeDeserialize<List<ResearchService.IdeaNote>>(idea.NotesJson);
        DateTimeOffset? lastNote = notes is { Count: > 0 } ? notes[^1].At : null;
        return (lastNote, LlmJson.SafeDeserialize<AppealMeta>(idea.AppealJson)?.At);
    }

    private sealed record AppealMeta(
        [property: JsonPropertyName("score_adjustments")] Dictionary<string, double>? ScoreAdjustments,
        [property: JsonPropertyName("at")] DateTimeOffset? At);
}
