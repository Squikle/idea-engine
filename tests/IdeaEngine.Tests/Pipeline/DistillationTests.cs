using IdeaEngine.Core.Common;
using IdeaEngine.Core.Pipeline;

namespace IdeaEngine.Tests.Pipeline;

public sealed class DistillationTests
{
    [Fact]
    public void Compute_AppealAdjustments_OverrideResearchCategories()
    {
        var research = new Dictionary<string, double>
        {
            ["demand"] = 0.3,
            ["willingness_to_pay"] = 0.4,
            ["feasibility_solo"] = 0.5,
            ["competition_gap"] = 0.5,
        };

        var plain = IdeaScoring.Compute(null, 0, research, 0.6);
        var adjusted = IdeaScoring.Compute(null, 0, research, 0.6,
            new Dictionary<string, double> { ["demand"] = 0.7 });

        Assert.False(plain.AppealAdjusted);
        Assert.True(adjusted.AppealAdjusted);
        Assert.True(adjusted.Total > plain.Total);
        Assert.Equal(0.7, adjusted.Categories["demand"], 3);
    }

    [Fact]
    public void Compute_AppealAdjustments_ApplyOverSkepticLayerToo()
    {
        var skeptic = new Dictionary<string, double>
        {
            ["demand"] = 0.2,
            ["willingness_to_pay"] = 0.3,
            ["feasibility_solo"] = 0.6,
            ["differentiation"] = 0.4,
        };

        // Pre-research appeal corrects demand: adjustment keys are research-namespace.
        var adjusted = IdeaScoring.Compute(skeptic, 0.7, null, 0,
            new Dictionary<string, double> { ["demand"] = 0.6 });

        Assert.Equal("skeptic", adjusted.Source);
        Assert.True(adjusted.AppealAdjusted);
        Assert.Equal(0.6, adjusted.Categories["demand"], 3);
    }

    [Fact]
    public void Staleness_EngineUpgrade_MarksStale()
    {
        // A report stamped before the newest milestone is stale by definition.
        Assert.True(Staleness.IsStale("0.27.0", DateTimeOffset.UtcNow, null, null));
    }

    [Fact]
    public void Staleness_NoteAfterReport_MarksStale()
    {
        var reportAt = DateTimeOffset.UtcNow.AddHours(-2);
        var latest = ReasoningMilestones.All[^1].Version.ToString(3);

        Assert.True(Staleness.IsStale(latest, reportAt, reportAt.AddHours(1), null));
        Assert.True(Staleness.IsStale(latest, reportAt, null, reportAt.AddMinutes(5)));
        Assert.False(Staleness.IsStale(latest, reportAt, reportAt.AddHours(-1), null));
    }

    [Fact]
    public void ConcernStatus_Glyphs()
    {
        Assert.Equal("🔥", Ui.ConcernStatus("fatal"));
        Assert.Equal("✅", Ui.ConcernStatus("mitigated"));
        Assert.Equal("🕊", Ui.ConcernStatus("waived"));
        Assert.Equal("🔓", Ui.ConcernStatus("open"));
        Assert.Equal("🔓", Ui.ConcernStatus(null));
    }
}
