namespace IdeaEngine.Core.Pipeline;

/// <summary>
/// The one place scoring formulas live. Computed on read (cheap at our scale) so
/// weights can be tuned without migrations; shown to the owner, so keep it explainable.
/// </summary>
public static class SignalScoring
{
    /// <summary>The wrist-strap rule, numerically: hate-but-they-buy outranks polite interest.</summary>
    public static double SentimentWeight(string commercialSentiment) => commercialSentiment switch
    {
        "buys_despite_complaints" => 1.1,
        "genuine_need" => 1.0,
        "nice_to_have" => 0.55,
        "no_market" => 0.1,
        _ => 0.7,
    };

    /// <summary>0..~1.1 — confidence carries, novelty boosts, sentiment gates.</summary>
    public static double Value(double confidence, double novelty, string commercialSentiment) =>
        Math.Round(
            Math.Clamp(confidence, 0, 1)
            * (0.6 + 0.4 * Math.Clamp(novelty, 0, 1))
            * SentimentWeight(commercialSentiment),
            4);
}

/// <summary>Composite rating for ideas from skeptic scores.</summary>
public static class IdeaScoring
{
    private static readonly (string Key, double Weight)[] Weights =
    [
        ("demand", 0.35),
        ("willingness_to_pay", 0.30),
        ("feasibility_solo", 0.20),
        ("differentiation", 0.15),
    ];

    /// <summary>
    /// 0..1 — weighted skeptic scores, discounted by skeptic confidence
    /// (an unsure skeptic should not mint a top-rated idea).
    /// </summary>
    public static double Rating(IReadOnlyDictionary<string, double>? scores, double skepticConfidence)
    {
        if (scores is not { Count: > 0 })
        {
            return 0;
        }

        double sum = 0;
        double weightSum = 0;
        foreach (var (key, weight) in Weights)
        {
            if (scores.TryGetValue(key, out var value))
            {
                sum += Math.Clamp(value, 0, 1) * weight;
                weightSum += weight;
            }
        }

        if (weightSum == 0)
        {
            return 0;
        }

        var confidenceFactor = 0.5 + 0.5 * Math.Clamp(skepticConfidence, 0, 1);
        return Math.Round(sum / weightSum * confidenceFactor, 3);
    }
}
