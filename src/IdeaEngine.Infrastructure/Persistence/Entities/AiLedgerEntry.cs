namespace IdeaEngine.Infrastructure.Persistence.Entities;

/// <summary>
/// One row per AI call. The budget guard aggregates per day/stage before every batch;
/// /costs reads it for reporting. Cheap to write, priceless when the bill surprises you.
/// </summary>
public sealed class AiLedgerEntry
{
    public long Id { get; set; }

    /// <summary>UTC date bucket for cheap aggregation.</summary>
    public DateOnly Day { get; set; }

    public required string Stage { get; set; }

    public required string Model { get; set; }

    public long TokensIn { get; set; }

    public long TokensOut { get; set; }

    public decimal CostUsd { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
