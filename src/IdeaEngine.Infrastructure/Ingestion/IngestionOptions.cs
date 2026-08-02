namespace IdeaEngine.Infrastructure.Ingestion;

/// <summary>Bound from configuration section <c>IdeaEngine:Ingestion</c>.</summary>
public sealed class IngestionOptions
{
    /// <summary>Run one ingestion cycle shortly after the worker starts.</summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>Hours between ingestion cycles.</summary>
    public double IntervalHours { get; set; } = 3;

    /// <summary>Per-source cap per cycle.</summary>
    public int MaxItemsPerSource { get; set; } = 60;

    /// <summary>
    /// Log every stored item at Information level ("water flow" mode).
    /// Set false once confidence is established to reduce log volume.
    /// </summary>
    public bool VerboseItemLogging { get; set; } = true;

    /// <summary>
    /// Send a Telegram summary after every cycle, even empty ones.
    /// When false, notifications go out only for cycles with new items or errors.
    /// </summary>
    public bool NotifyEveryCycle { get; set; } = true;
}
