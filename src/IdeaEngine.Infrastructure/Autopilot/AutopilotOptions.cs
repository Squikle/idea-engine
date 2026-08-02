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

    /// <summary>Max research jobs auto-queued per scheduled run. Every candidate above the
    /// floor gets its own job - opportunities are judged individually, never as a tournament.</summary>
    public int AutoResearchTop { get; set; } = 8;

    /// <summary>Absolute floor: candidates below this estimate are listed as skipped (with a
    /// force hint), not silently dropped. Deliberately low - missing gold costs more than $0.25.</summary>
    public double MinRatingForResearch { get; set; } = 0.30;

    /// <summary>Daily digest, local time (default 21:00 Ontario).</summary>
    public string DigestTime { get; set; } = "21:00";
}
