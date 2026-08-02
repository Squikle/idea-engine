using IdeaEngine.Core.Common;
using IdeaEngine.Core.Pipeline;

namespace IdeaEngine.Tests.Pipeline;

public sealed class ReevalScoringTests
{
    private static ReevalSnapshot Snapshot(
        int missed = 0, int open = 0, string verdict = "maybe", double score = 0.3,
        bool notesAfter = false, bool appealShallow = false, bool relations = false, double age = 5) =>
        new(1, "t", missed, open, verdict, score, notesAfter, appealShallow, relations, age);

    [Fact]
    public void FreshCleanVerdict_ScoresNearZero()
    {
        var (priority, reasons) = ReevalScoring.Score(Snapshot());

        Assert.True(priority < 0.25, $"expected below shortlist floor, got {priority}");
        Assert.Empty(reasons);
    }

    [Fact]
    public void OldBrainKillWithDecentScore_IsTopPriority()
    {
        var (priority, reasons) = ReevalScoring.Score(
            Snapshot(missed: 4, open: 2, verdict: "no-go", score: 0.5));

        Assert.True(priority >= 0.9, $"got {priority}");
        Assert.Contains(reasons, r => r.Contains("upgrade", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("killed despite", StringComparison.Ordinal));
    }

    [Fact]
    public void NotesAfterVerdict_AloneCrossesTheFloor()
    {
        var (priority, reasons) = ReevalScoring.Score(Snapshot(notesAfter: true));

        Assert.True(priority >= 0.25);
        Assert.Contains(reasons, r => r.Contains("notes", StringComparison.Ordinal));
    }

    [Fact]
    public void Backlog_OperatorDropAgedWithDecentEstimate_Prioritized()
    {
        var (priority, reasons) = ReevalScoring.ScoreBacklog(0.5, 10, "operator", false);

        Assert.True(priority >= 0.6, $"got {priority}");
        Assert.Contains(reasons, r => r.Contains("never researched", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("your own drop", StringComparison.Ordinal));
    }

    [Fact]
    public void Milestones_NullVersionMissesEverything_CurrentMissesNothing()
    {
        Assert.Equal(ReasoningMilestones.All.Count, ReasoningMilestones.MissedSince(null).Count);
        Assert.Empty(ReasoningMilestones.MissedSince("9.9.9"));
        Assert.Contains(
            ReasoningMilestones.MissedSince("0.11.0"),
            m => m.Summary.Contains("debate", StringComparison.Ordinal));
    }
}
