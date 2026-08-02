using IdeaEngine.Core.Pipeline;

namespace IdeaEngine.Tests.Pipeline;

public sealed class ScoringTests
{
    [Fact]
    public void SignalValue_WristStrapRule_HateThatBuysOutranksPoliteInterest()
    {
        var buysDespiteHate = SignalScoring.Value(0.8, 0.5, "buys_despite_complaints");
        var niceToHave = SignalScoring.Value(0.8, 0.5, "nice_to_have");
        var noMarket = SignalScoring.Value(0.8, 0.5, "no_market");

        Assert.True(buysDespiteHate > niceToHave);
        Assert.True(niceToHave > noMarket);
    }

    [Fact]
    public void SignalValue_NoveltyBoosts_ButConfidenceCarries()
    {
        var confidentOld = SignalScoring.Value(0.9, 0.0, "genuine_need");
        var confidentNovel = SignalScoring.Value(0.9, 1.0, "genuine_need");
        var unsureNovel = SignalScoring.Value(0.2, 1.0, "genuine_need");

        Assert.True(confidentNovel > confidentOld);
        Assert.True(confidentOld > unsureNovel);
    }

    [Fact]
    public void SignalValue_ClampsOutOfRangeInputs()
    {
        var value = SignalScoring.Value(5.0, -2.0, "genuine_need");

        Assert.Equal(0.6, value, 3); // clamped: 1.0 * (0.6 + 0.4*0) * 1.0
    }

    [Fact]
    public void IdeaRating_WeightsScores_AndDiscountsByConfidence()
    {
        var scores = new Dictionary<string, double>
        {
            ["demand"] = 1.0,
            ["willingness_to_pay"] = 1.0,
            ["feasibility_solo"] = 1.0,
            ["differentiation"] = 1.0,
        };

        Assert.Equal(1.0, IdeaScoring.Rating(scores, 1.0), 3);
        Assert.Equal(0.5, IdeaScoring.Rating(scores, 0.0), 3); // unsure skeptic halves it
    }

    [Fact]
    public void IdeaRating_PartialScores_Renormalize()
    {
        var scores = new Dictionary<string, double> { ["demand"] = 0.8 };

        Assert.Equal(0.8, IdeaScoring.Rating(scores, 1.0), 3);
    }

    [Theory]
    [InlineData(null)]
    public void IdeaRating_NoScores_IsZero(Dictionary<string, double>? scores)
    {
        Assert.Equal(0, IdeaScoring.Rating(scores, 1.0));
    }

    [Fact]
    public void Compute_ResearchOverridesSkeptic_AndUsesResearchConfidence()
    {
        var skeptic = new Dictionary<string, double>
        {
            ["demand"] = 0.2, ["willingness_to_pay"] = 0.2, ["feasibility_solo"] = 0.2, ["differentiation"] = 0.2,
        };
        var research = new Dictionary<string, double>
        {
            ["demand"] = 1.0, ["willingness_to_pay"] = 1.0, ["feasibility_solo"] = 1.0, ["competition_gap"] = 1.0,
        };

        var score = IdeaScoring.Compute(skeptic, 0.9, research, 1.0);

        Assert.Equal("research", score.Source);
        Assert.Equal(1.0, score.Total, 3); // all 1.0 × full confidence
        Assert.Equal(1.0, score.Categories["gap"], 3); // competition_gap mapped to unified "gap"
    }

    [Fact]
    public void Compute_SkepticFallback_MapsDifferentiationToGap()
    {
        var skeptic = new Dictionary<string, double>
        {
            ["demand"] = 0.8, ["differentiation"] = 0.4,
        };

        var score = IdeaScoring.Compute(skeptic, 1.0, null, 0);

        Assert.Equal("skeptic", score.Source);
        Assert.Equal(0.4, score.Categories["gap"], 3);
        // weighted: (0.8*0.35 + 0.4*0.15) / 0.50 = 0.68, confidence factor 1.0
        Assert.Equal(0.68, score.Total, 3);
    }

    [Fact]
    public void Compute_LowConfidence_DiscountsTotal()
    {
        var research = new Dictionary<string, double> { ["demand"] = 1.0 };

        var confident = IdeaScoring.Compute(null, 0, research, 1.0);
        var unsure = IdeaScoring.Compute(null, 0, research, 0.0);

        Assert.Equal(1.0, confident.Total, 3);
        Assert.Equal(0.5, unsure.Total, 3);
    }

    [Fact]
    public void Compute_NothingScored_IsNoneWithZeroTotal()
    {
        var score = IdeaScoring.Compute(null, 0.5, null, 0);

        Assert.Equal(0, score.Total);
        Assert.Empty(score.Categories);
    }
}
