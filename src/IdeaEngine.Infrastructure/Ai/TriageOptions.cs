namespace IdeaEngine.Infrastructure.Ai;

/// <summary>Bound from configuration section <c>IdeaEngine:Ai:Triage</c>.</summary>
public sealed class TriageOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>OpenRouter model id.</summary>
    public string Model { get; set; } = "openai/gpt-5-nano";

    /// <summary>USD per million input tokens (for ledger cost computation).</summary>
    public decimal InputPricePerMTok { get; set; } = 0.05m;

    /// <summary>USD per million output tokens.</summary>
    public decimal OutputPricePerMTok { get; set; } = 0.40m;

    /// <summary>Hard daily spend cap for this stage; stage pauses until midnight UTC.</summary>
    public decimal DailyUsdCap { get; set; } = 1.00m;

    /// <summary>Items claimed per processing round.</summary>
    public int BatchSize { get; set; } = 8;

    /// <summary>Concurrent model calls within a round.</summary>
    public int Parallelism { get; set; } = 4;

    /// <summary>Send a Telegram summary (with top signals) after each drain that found any.</summary>
    public bool NotifyAfterDrain { get; set; } = true;

    public int PollSecondsBusy { get; set; } = 15;

    public int PollSecondsIdle { get; set; } = 120;

    /// <summary>
    /// Completion budget per call. Reasoning models spend tokens thinking BEFORE emitting
    /// content; too small a budget truncates the JSON (the 2026-08-02 0-signals incident).
    /// </summary>
    public int MaxCompletionTokens { get; set; } = 3000;

    /// <summary>Reasoning effort hint (low|medium|high). Extraction needs "low".</summary>
    public string ReasoningEffort { get; set; } = "low";

    /// <summary>Content truncation before prompting.</summary>
    public int MaxBodyChars { get; set; } = 2500;

    public int MaxCommentsInPrompt { get; set; } = 8;

    public int MaxCommentChars { get; set; } = 400;
}
