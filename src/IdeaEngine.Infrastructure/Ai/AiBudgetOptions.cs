namespace IdeaEngine.Infrastructure.Ai;

/// <summary>
/// Bound from configuration section <c>IdeaEngine:Ai:Budget</c>.
/// Global financial firewall on top of per-stage daily caps and provider-side key limits.
/// </summary>
public sealed class AiBudgetOptions
{
    /// <summary>All AI stages combined, per UTC day.</summary>
    public decimal GlobalDailyUsdCap { get; set; } = 5.00m;

    /// <summary>All AI stages combined, per calendar month (UTC).</summary>
    public decimal GlobalMonthlyUsdCap { get; set; } = 60.00m;

    /// <summary>
    /// Refuse any single call whose worst-case cost estimate exceeds this.
    /// Catches misconfiguration (wrong model/price/token budget) before money moves.
    /// </summary>
    public decimal MaxUsdPerCall { get; set; } = 0.15m;
}
