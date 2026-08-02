using System.Text.Json;
using System.Text.Json.Serialization;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace IdeaEngine.Infrastructure.Jobs;

public sealed record DropJobPayload(
    [property: JsonPropertyName("pitch")] string Pitch,
    [property: JsonPropertyName("idea_id")] long? IdeaId);

public sealed record ResearchJobPayload(
    [property: JsonPropertyName("idea_id")] long IdeaId);

/// <summary>Enqueue/claim/checkpoint operations for the durable job queue.</summary>
public sealed class JobService(IdeaEngineDbContext db, TimeProvider timeProvider)
{
    public async Task<(long JobId, int Position)> EnqueueAsync<T>(
        string kind, T payload, int? originMessageId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var ahead = await db.Jobs.CountAsync(
            j => j.Status == "queued" || j.Status == "running", cancellationToken);
        var job = new JobEntity
        {
            Kind = kind,
            PayloadJson = JsonSerializer.Serialize(payload, LlmJson.Options),
            Status = "queued",
            OriginMessageId = originMessageId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return (job.Id, ahead + 1);
    }

    public async Task<bool> RetryAsync(long jobId, CancellationToken cancellationToken) =>
        await db.Jobs
            .Where(j => j.Id == jobId && (j.Status == "failed" || j.Status == "done"))
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, "queued")
                    .SetProperty(j => j.Attempts, 0)
                    .SetProperty(j => j.LastError, (string?)null)
                    .SetProperty(j => j.UpdatedAt, timeProvider.GetUtcNow()),
                cancellationToken) > 0;

    /// <summary>Startup recovery: anything left "running" by a dead process goes back to the queue.</summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken) =>
        await db.Jobs
            .Where(j => j.Status == "running")
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, "queued")
                    .SetProperty(j => j.UpdatedAt, timeProvider.GetUtcNow()),
                cancellationToken);

    public async Task<JobEntity?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var job = await db.Jobs
            .Where(j => j.Status == "queued")
            .OrderBy(j => j.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return null;
        }

        job.Status = "running";
        job.Attempts++;
        job.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task SetProgressMessageAsync(long jobId, int? messageId, CancellationToken cancellationToken) =>
        await db.Jobs
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.ProgressMessageId, messageId)
                    .SetProperty(j => j.UpdatedAt, timeProvider.GetUtcNow()),
                cancellationToken);

    public async Task CheckpointAsync<T>(long jobId, T payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, LlmJson.Options);
        await db.Jobs
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.PayloadJson, json)
                    .SetProperty(j => j.UpdatedAt, timeProvider.GetUtcNow()),
                cancellationToken);
    }

    public async Task CompleteAsync(long jobId, string? error, CancellationToken cancellationToken)
    {
        var failedFinally = error is not null;
        await db.Jobs
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, failedFinally ? "failed" : "done")
                    .SetProperty(j => j.LastError, error)
                    .SetProperty(j => j.UpdatedAt, timeProvider.GetUtcNow()),
                cancellationToken);
    }

    public async Task RequeueForRetryAsync(long jobId, string error, CancellationToken cancellationToken) =>
        await db.Jobs
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, "queued")
                    .SetProperty(j => j.LastError, error)
                    .SetProperty(j => j.UpdatedAt, timeProvider.GetUtcNow()),
                cancellationToken);
}
