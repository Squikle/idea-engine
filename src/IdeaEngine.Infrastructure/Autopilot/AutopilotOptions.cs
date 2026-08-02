namespace IdeaEngine.Infrastructure.Autopilot;

/// <summary>
/// Bound from configuration section <c>IdeaEngine:Autopilot</c>.
/// Times are LOCAL wall-clock in the configured time zone (IdeaEngine:TimeZone,
/// default America/Toronto) - DST handled automatically.
/// </summary>
public sealed class AutopilotOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Daily scheduled ideation, local time (default 10:00 Ontario).</summary>
    public string IdeationTime { get; set; } = "10:00";

    /// <summary>Builder-vs-skeptic sessions per scheduled run.</summary>
    public int SessionsPerDay { get; set; } = 3;

    /// <summary>Best fresh candidates auto-researched after the scheduled ideation.</summary>
    public int AutoResearchTop { get; set; } = 1;

    /// <summary>Candidates below this rating are not worth web-research money.</summary>
    public double MinRatingForResearch { get; set; } = 0.45;

    /// <summary>Daily digest, local time (default 21:00 Ontario).</summary>
    public string DigestTime { get; set; } = "21:00";
}
