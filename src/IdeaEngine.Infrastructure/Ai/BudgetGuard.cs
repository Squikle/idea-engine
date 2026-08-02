using IdeaEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Ai;

/// <summary>Verdict of a pre-spend check. When blocked, <see cref="Reason"/> says exactly why.</summary>
public sealed record BudgetCheck(
    bool Allowed,
    string? Reason,
    decimal StageSpentToday,
    decimal GlobalSpentToday,
    decimal GlobalSpentMonth);

/// <summary>
/// Consulted BEFORE every AI call batch. Layers, all from ai_ledger (source of truth):
/// per-stage daily cap → global daily cap → global monthly cap → per-call sanity ceiling.
/// Worst case overshoot is bounded by one in-flight batch.
/// </summary>
public sealed class BudgetGuard(
    IdeaEngineDbContext db,
    TimeProvider timeProvider,
    IOptions<AiBudgetOptions> budgetOptions)
{
    /// <param name="worstCallUsd">Worst-case cost of a single upcoming call (sanity ceiling).</param>
    /// <param name="plannedSpendUsd">Worst-case cost of the whole upcoming batch (cap projection).</param>
    public async Task<BudgetCheck> CheckAsync(
        string stage,
        decimal stageDailyCap,
        decimal worstCallUsd,
        decimal plannedSpendUsd,
        CancellationToken cancellationToken)
    {
        var options = budgetOptions.Value;
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        var monthRows = await db.AiLedger
            .Where(e => e.Day >= monthStart)
            .GroupBy(e => new { IsToday = e.Day == today, IsStage = e.Stage == stage })
            .Select(g => new { g.Key.IsToday, g.Key.IsStage, Cost = g.Sum(e => e.CostUsd) })
            .ToListAsync(cancellationToken);

        var stageToday = monthRows.Where(r => r.IsToday && r.IsStage).Sum(r => r.Cost);
        var globalToday = monthRows.Where(r => r.IsToday).Sum(r => r.Cost);
        var globalMonth = monthRows.Sum(r => r.Cost);

        string? reason = null;
        if (worstCallUsd > options.MaxUsdPerCall)
        {
            reason = $"single call estimate ${worstCallUsd:F3} exceeds MaxUsdPerCall ${options.MaxUsdPerCall:F2} (misconfiguration?)";
        }
        else if (stageToday >= stageDailyCap)
        {
            reason = $"stage '{stage}' daily cap ${stageDailyCap:F2} reached (spent ${stageToday:F2})";
        }
        else if (globalToday + plannedSpendUsd > options.GlobalDailyUsdCap)
        {
            reason = $"global daily cap ${options.GlobalDailyUsdCap:F2} reached (spent ${globalToday:F2})";
        }
        else if (globalMonth + plannedSpendUsd > options.GlobalMonthlyUsdCap)
        {
            reason = $"global monthly cap ${options.GlobalMonthlyUsdCap:F2} reached (spent ${globalMonth:F2})";
        }

        return new BudgetCheck(reason is null, reason, stageToday, globalToday, globalMonth);
    }
}
