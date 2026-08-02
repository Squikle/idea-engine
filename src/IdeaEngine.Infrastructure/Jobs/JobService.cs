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
    public async Task<long> EnqueueAsync<T>(string kind, T payload, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var job = new JobEntity
        {
            Kind = kind,
            PayloadJson = JsonSerializer.Serialize(payload, LlmJson.Options),
            Status = "queued",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job.Id;
    }

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
