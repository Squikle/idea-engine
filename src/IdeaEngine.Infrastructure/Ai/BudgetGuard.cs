using System.Globalization;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
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
    private const string BumpKeyPrefix = "budget_bump:";

    /// <summary>Owner-triggered temporary raise: adds to today's stage AND global daily caps
    /// (the monthly cap stays a hard ceiling on purpose). Returns the new total bump.</summary>
    public async Task<decimal> BumpTodayAsync(decimal amount, CancellationToken cancellationToken)
    {
        var key = BumpKeyPrefix + DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var state = await db.AppState.FindAsync([key], cancellationToken);
        var total = amount + (state is null
            ? 0
            : decimal.TryParse(state.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0);

        if (state is null)
        {
            db.AppState.Add(new AppStateEntity
            {
                Key = key,
                Value = total.ToString(CultureInfo.InvariantCulture),
                UpdatedAt = timeProvider.GetUtcNow(),
            });
        }
        else
        {
            state.Value = total.ToString(CultureInfo.InvariantCulture);
            state.UpdatedAt = timeProvider.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
        return total;
    }

    public async Task<decimal> GetTodayBumpAsync(CancellationToken cancellationToken)
    {
        var key = BumpKeyPrefix + DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var state = await db.AppState.FindAsync([key], cancellationToken);
        return state is not null
            && decimal.TryParse(state.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : 0;
    }

    public async Task<BudgetCheck> CheckAsync(
        string stage,
        decimal stageDailyCap,
        decimal worstCallUsd,
        decimal plannedSpendUsd,
        CancellationToken cancellationToken)
    {
        var options = budgetOptions.Value;
        var bump = await GetTodayBumpAsync(cancellationToken);
        stageDailyCap += bump;
        var globalDailyCap = options.GlobalDailyUsdCap + bump;
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
        else if (globalToday + plannedSpendUsd > globalDailyCap)
        {
            reason = $"global daily cap ${globalDailyCap:F2} reached (spent ${globalToday:F2})";
        }
        else if (globalMonth + plannedSpendUsd > options.GlobalMonthlyUsdCap)
        {
            reason = $"global monthly cap ${options.GlobalMonthlyUsdCap:F2} reached (spent ${globalMonth:F2})";
        }

        return new BudgetCheck(reason is null, reason, stageToday, globalToday, globalMonth);
    }
}
