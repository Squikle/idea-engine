namespace IdeaEngine.Core.Common;

/// <summary>
/// The bot's visual vocabulary - one emoji per concept, used consistently across every
/// surface so the owner scans by shape, not by reading. Change here, changes everywhere.
/// </summary>
public static class Ui
{
    // Worker state
    public const string Live = "🟢";
    public const string Offline = "🔴";

    // Activities
    public const string Collect = "📥";
    public const string Analyze = "🧠";
    public const string Ideate = "💡";
    public const string Research = "🔎";
    public const string Advise = "🧭";
    public const string Idle = "😴";

    // Money & alerts
    public const string Spend = "💸";
    public const string Stopped = "⛔";
    public const string Done = "✅";
    public const string Warning = "🟠";
    public const string Error = "🔴";

    // Content
    public const string Digest = "🗞";
    public const string Best = "🏆";
    public const string Signal = "🎯";
    public const string Release = "🚀";
    public const string Journal = "📓";
    public const string Drop = "📦";

    /// <summary>Signal kind → scan-anchor.</summary>
    public static string Kind(string kind) => kind switch
    {
        "pain" => "🩹",
        "wish" => "✨",
        "demand" => "💰",
        "trend" => "📈",
        "complaint" => "😤",
        _ => Signal,
    };

    /// <summary>Idea status → scan-anchor.</summary>
    public static string IdeaStatus(string status) => status switch
    {
        "hot" => "🔥",
        "validated" => "✅",
        "candidate" => "🟡",
        _ => "☠️",
    };

    /// <summary>Research verdict → colored label.</summary>
    public static string Verdict(string? verdict) => verdict?.ToUpperInvariant() switch
    {
        "GO" => "🟢 GO",
        "NO-GO" => "🔴 NO-GO",
        _ => "🟡 MAYBE",
    };

    /// <summary>Status-board activity icon.</summary>
    public static string Activity(string activity) => activity switch
    {
        "Collecting" => Collect,
        "Analyzing" => Analyze,
        "Ideating" => Ideate,
        "Researching" => Research,
        "Advising" => Advise,
        "Idle" => Idle,
        _ => Live,
    };
}
