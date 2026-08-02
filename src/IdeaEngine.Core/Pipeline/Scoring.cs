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

/// <summary>
/// THE idea score - one number everywhere. Category values come from web research when it
/// exists (evidence beats opinion), else from the skeptic. Confidence discounts the total.
/// </summary>
public sealed record IdeaScore(
    double Total,
    double Confidence,
    string Source,
    IReadOnlyDictionary<string, double> Categories)
{
    public static readonly IdeaScore None = new(0, 0, "none", new Dictionary<string, double>());
}

/// <summary>Composite rating for ideas from skeptic scores.</summary>
public static class IdeaScoring
{
    /// <summary>Unified category keys with weights (research key ↔ skeptic key mapping below).</summary>
    private static readonly (string Key, string ResearchKey, string SkepticKey, double Weight)[] Unified =
    [
        ("demand", "demand", "demand", 0.35),
        ("pay", "willingness_to_pay", "willingness_to_pay", 0.30),
        ("build", "feasibility_solo", "feasibility_solo", 0.20),
        ("gap", "competition_gap", "differentiation", 0.15),
    ];

    /// <summary>Single source of truth for "is this idea good": /ideas, /idea, autopilot.</summary>
    public static IdeaScore Compute(
        IReadOnlyDictionary<string, double>? skepticScores,
        double skepticConfidence,
        IReadOnlyDictionary<string, double>? researchScores,
        double researchConfidence)
    {
        var useResearch = researchScores is { Count: > 0 };
        var source = useResearch ? "research" : skepticScores is { Count: > 0 } ? "skeptic" : "none";
        var confidence = Math.Clamp(useResearch ? researchConfidence : skepticConfidence, 0, 1);

        var categories = new Dictionary<string, double>();
        double sum = 0;
        double weightSum = 0;
        foreach (var (key, researchKey, skepticKey, weight) in Unified)
        {
            double? value = null;
            if (useResearch && researchScores!.TryGetValue(researchKey, out var fromResearch))
            {
                value = fromResearch;
            }
            else if (skepticScores is not null && skepticScores.TryGetValue(skepticKey, out var fromSkeptic))
            {
                value = fromSkeptic;
            }

            if (value is { } present)
            {
                var clamped = Math.Clamp(present, 0, 1);
                categories[key] = clamped;
                sum += clamped * weight;
                weightSum += weight;
            }
        }

        if (weightSum == 0)
        {
            return IdeaScore.None with { Source = source, Confidence = confidence };
        }

        var total = Math.Round(sum / weightSum * (0.5 + 0.5 * confidence), 3);
        return new IdeaScore(total, confidence, source, categories);
    }

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
