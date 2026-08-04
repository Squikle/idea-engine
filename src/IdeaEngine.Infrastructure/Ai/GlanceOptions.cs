namespace IdeaEngine.Infrastructure.Ai;

/// <summary>Bound from configuration section <c>IdeaEngine:Ai:Glance</c>.</summary>
public sealed class GlanceOptions
{
    public string Model { get; set; } = "openai/gpt-5-nano";

    /// <summary>Copy with a runtime model override applied.</summary>
    public GlanceOptions WithModel(ResolvedModel resolved)
    {
        var clone = (GlanceOptions)MemberwiseClone();
        clone.Model = resolved.Model;
        clone.InputPricePerMTok = resolved.InPerMTok;
        clone.OutputPricePerMTok = resolved.OutPerMTok;
        return clone;
    }

    public decimal InputPricePerMTok { get; set; } = 0.05m;

    public decimal OutputPricePerMTok { get; set; } = 0.40m;

    /// <summary>Tiny stage cap - glances cost fractions of a cent and are cached forever.</summary>
    public decimal DailyUsdCap { get; set; } = 0.10m;

    public int MaxCompletionTokens { get; set; } = 900;
}
