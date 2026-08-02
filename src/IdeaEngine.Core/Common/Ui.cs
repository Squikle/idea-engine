namespace IdeaEngine.Core.Common;

/// <summary>
/// The bot's visual vocabulary - one emoji per concept, used consistently across every
/// surface so the owner scans by shape, not by reading. Change here, changes everywhere.
/// </summary>
public static class Ui
{
    // Worker state
    public static readonly string Live = "🟢";
    public static readonly string Offline = "🔴";

    // Activities
    public static readonly string Collect = "📥";
    public static readonly string Analyze = "🧠";
    public static readonly string Ideate = "💡";
    public static readonly string Research = "🔎";
    public static readonly string Advise = "🧭";
    public static readonly string Idle = "😴";

    // Money & alerts
    public static readonly string Spend = "💸";
    public static readonly string Stopped = "⛔";
    public static readonly string Done = "✅";
    public static readonly string Warning = "🟠";
    public static readonly string Error = "🔴";

    // Content
    public static readonly string Digest = "🗞";
    public static readonly string Best = "🏆";
    public static readonly string Signal = "🎯";
    public static readonly string Release = "🚀";
    public static readonly string Journal = "📓";
    public static readonly string Drop = "📦";

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
        "uncertain" or "validated" => "🤔",
        "candidate" => "🌱",
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
