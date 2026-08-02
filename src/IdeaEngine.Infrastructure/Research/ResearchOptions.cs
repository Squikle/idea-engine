namespace IdeaEngine.Infrastructure.Research;

/// <summary>Bound from configuration section <c>IdeaEngine:Ai:Research</c>.</summary>
public sealed class ResearchOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Plans queries and synthesizes the final report.</summary>
    public string Model { get; set; } = "anthropic/claude-sonnet-5";

    public decimal InputPricePerMTok { get; set; } = 2.00m;

    public decimal OutputPricePerMTok { get; set; } = 10.00m;

    /// <summary>Stage daily cap (stage name: "research"). ~$0.05-0.10 per report.</summary>
    public decimal DailyUsdCap { get; set; } = 2.00m;

    /// <summary>Web queries per report (Brave free tier: ~1000 searches/month).</summary>
    public int MaxQueries { get; set; } = 6;

    public int ResultsPerQuery { get; set; } = 5;

    /// <summary>Brave free plan allows 1 request/second.</summary>
    public int SearchDelayMs { get; set; } = 1100;

    public int MaxCompletionTokens { get; set; } = 6000;

    public string ReasoningEffort { get; set; } = "medium";
}
