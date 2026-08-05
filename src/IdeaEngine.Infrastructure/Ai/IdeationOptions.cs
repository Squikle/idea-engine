namespace IdeaEngine.Infrastructure.Ai;

/// <summary>Bound from configuration section <c>IdeaEngine:Ai:Ideation</c>.</summary>
public sealed class IdeationOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Proposes ideas. Strong model, cross-vendor with the skeptic on purpose.</summary>
    public string BuilderModel { get; set; } = "anthropic/claude-sonnet-5";

    public decimal BuilderInputPricePerMTok { get; set; } = 2.00m;

    public decimal BuilderOutputPricePerMTok { get; set; } = 10.00m;

    /// <summary>Attacks ideas. Different vendor reduces correlated blind spots.</summary>
    public string SkepticModel { get; set; } = "deepseek/deepseek-v4-pro";

    public decimal SkepticInputPricePerMTok { get; set; } = 0.43m;

    public decimal SkepticOutputPricePerMTok { get; set; } = 0.87m;

    /// <summary>Copy with runtime model overrides applied (never mutate the bound singleton).</summary>
    public IdeationOptions WithModels(ResolvedModel builder, ResolvedModel skeptic)
    {
        var clone = (IdeationOptions)MemberwiseClone();
        clone.BuilderModel = builder.Model;
        clone.BuilderInputPricePerMTok = builder.InPerMTok;
        clone.BuilderOutputPricePerMTok = builder.OutPerMTok;
        clone.SkepticModel = skeptic.Model;
        clone.SkepticInputPricePerMTok = skeptic.InPerMTok;
        clone.SkepticOutputPricePerMTok = skeptic.OutPerMTok;
        clone.ReasoningEffort = builder.Effort ?? clone.ReasoningEffort;
        return clone;
    }

    /// <summary>Stage daily cap (stage name: "ideation").</summary>
    public decimal DailyUsdCap { get; set; } = 3.00m;

    /// <summary>Hard cap for /ideate argument.</summary>
    public int MaxSessionsPerCommand { get; set; } = 10;

    /// <summary>Signals sampled into each session's grounding (varied per session).</summary>
    public int SignalsPerSession { get; set; } = 24;

    /// <summary>Pool of best recent signals that sessions sample from.</summary>
    public int SignalPoolSize { get; set; } = 80;

    public double MinSignalConfidence { get; set; } = 0.35;

    /// <summary>Random long-tail signals blended into each pool (below the top cut).</summary>
    public int TailSampleSize { get; set; } = 20;

    /// <summary>Confidence floor for tail signals.</summary>
    public double TailMinConfidence { get; set; } = 0.30;

    public int MaxCompletionTokens { get; set; } = 4000;

    public string ReasoningEffort { get; set; } = "medium";
}
