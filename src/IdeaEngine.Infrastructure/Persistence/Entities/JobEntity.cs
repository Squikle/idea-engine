namespace IdeaEngine.Infrastructure.Persistence.Entities;

/// <summary>
/// Durable command queue: /drop and /research become jobs that survive restarts.
/// Interrupted (running) jobs are re-queued on startup; payload carries checkpoints
/// so completed stages are never re-run.
/// </summary>
public sealed class JobEntity
{
    public long Id { get; set; }

    /// <summary>drop | research.</summary>
    public required string Kind { get; set; }

    /// <summary>Stage checkpoints live here too (e.g. drop stores IdeaId once shaped).</summary>
    public required string PayloadJson { get; set; }

    /// <summary>queued | running | done | failed.</summary>
    public required string Status { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    /// <summary>Telegram message id of the queue-ack; progress replies to it so the
    /// owner can tap through from ack to the live log.</summary>
    public int? OriginMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
