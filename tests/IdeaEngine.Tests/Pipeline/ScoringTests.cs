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
}
