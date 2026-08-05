using System.Globalization;

namespace IdeaEngine.Infrastructure.Autopilot;

/// <summary>One runtime-adjustable setting: stored in app_state as setting.&lt;key&gt;.</summary>
public sealed record SettingSpec(
    string Key,
    string Description,
    string Kind, // int | ratio | time
    string Allowed,
    Func<AutopilotOptions, string> DefaultValue);

/// <summary>
/// The single whitelist of runtime settings - /config, the right hand and the autopilot
/// all read THIS. A setting not listed here cannot be changed at runtime, period.
/// </summary>
public static class SettingsCatalog
{
    public static readonly IReadOnlyList<SettingSpec> All =
    [
        new("sessions_per_day", "ideation sessions per autopilot run", "int", "1-10",
            o => o.SessionsPerDay.ToString(CultureInfo.InvariantCulture)),
        new("auto_research_top", "max research jobs auto-queued per run", "int", "0-20",
            o => o.AutoResearchTop.ToString(CultureInfo.InvariantCulture)),
        new("min_rating_for_research", "absolute floor for auto-research", "ratio", "0.05-0.95",
            o => o.MinRatingForResearch.ToString("0.##", CultureInfo.InvariantCulture)),
        new("ideation_time", "daily ideation, local Ontario time", "time", "HH:mm",
            o => o.IdeationTime),
        new("mine_time", "daily /mine auto-run, local time", "time", "HH:mm",
            o => o.MineTime),
        new("digest_time", "daily digest, local time", "time", "HH:mm",
            o => o.DigestTime),
    ];

    public static SettingSpec? Find(string key) =>
        All.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Validates a raw value against the spec. Null = valid.</summary>
    public static string? Validate(SettingSpec spec, string value) => spec.Kind switch
    {
        "int" when !int.TryParse(value, out var i) || i < 0 || i > 50 => "not a valid integer",
        "ratio" when !double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            || d is <= 0 or >= 1 => "must be a number between 0 and 1 (exclusive)",
        "time" when !TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _) => "must be HH:mm (24h)",
        _ => null,
    };
}
