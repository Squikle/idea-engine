using System.Text.Json;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ideation;
using IdeaEngine.Infrastructure.Jobs;
using IdeaEngine.Infrastructure.Persistence.Entities;
using IdeaEngine.Infrastructure.Research;

namespace IdeaEngine.Worker;

/// <summary>
/// Executes durable jobs one at a time. On startup, jobs left "running" by a dead
/// process are re-queued (auto-recovery); the drop pipeline checkpoints the created
/// idea id, so a restart resumes at research instead of re-shaping.
/// </summary>
internal sealed class JobRunnerHostedService(
    IServiceScopeFactory scopeFactory,
    ResearchCoordinator researchCoordinator,
    IProgressNotifier progressNotifier,
    INotifier notifier,
    ILogger<JobRunnerHostedService> logger) : BackgroundService
{
    private const int MaxAttempts = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        await RecoverAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            JobEntity? job = null;
            try
            {
                using (var scope = scopeFactory.CreateScope())
                {
                    job = await scope.ServiceProvider.GetRequiredService<JobService>()
                        .ClaimNextAsync(stoppingToken);
                }

                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
                    continue;
                }

                await ExecuteJobAsync(job, stoppingToken);
                await FinishAsync(job.Id, error: null, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return; // job stays "running"; startup recovery re-queues it
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job {JobId} ({Kind}) crashed", job?.Id, job?.Kind);
                if (job is not null)
                {
                    if (job.Attempts >= MaxAttempts)
                    {
                        await FinishAsync(job.Id, error: ex.Message, stoppingToken);
                        await notifier.SendAsync(
                            $"⛔ Job #{job.Id} ({job.Kind}) failed after {job.Attempts} attempts — check logs.",
                            CancellationToken.None);
                    }
                    else
                    {
                        await RequeueAsync(job.Id, ex.Message, stoppingToken);
                    }
                }
            }
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var recovered = await scope.ServiceProvider.GetRequiredService<JobService>()
                .RecoverInterruptedAsync(cancellationToken);
            if (recovered > 0)
            {
                logger.LogInformation("Recovered {Count} interrupted job(s) after restart", recovered);
                await notifier.SendAsync(
                    $"♻️ Recovered {recovered} interrupted job(s) after restart — resuming from checkpoints.",
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Job recovery failed");
        }
    }

    private async Task ExecuteJobAsync(JobEntity job, CancellationToken cancellationToken)
    {
        switch (job.Kind)
        {
            case "drop":
                await ExecuteDropAsync(job, cancellationToken);
                break;
            case "research":
                await ExecuteResearchAsync(job, cancellationToken);
                break;
            default:
                logger.LogWarning("Unknown job kind {Kind} (#{JobId}), marking done", job.Kind, job.Id);
                break;
        }
    }

    private async Task ExecuteDropAsync(JobEntity job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<DropJobPayload>(job.PayloadJson, LlmJson.Options)
            ?? throw new InvalidOperationException("drop payload unreadable");

        var resumed = payload.IdeaId is not null;
        var progress = await progressNotifier.StartAsync(
            resumed
                ? $"📦 Drop (job #{job.Id}) · resuming at research for idea #{payload.IdeaId}…"
                : $"📦 Drop (job #{job.Id}) · shaping your pitch…",
            cancellationToken);

        var ideaId = payload.IdeaId;

        if (ideaId is null)
        {
            await OperationGates.Ideation.WaitAsync(cancellationToken);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var ideation = scope.ServiceProvider.GetRequiredService<IdeationService>();
                var shaped = await ideation.RunOperatorIdeaAsync(payload.Pitch, progress, cancellationToken);
                if (shaped.StoppedReason is { } reason)
                {
                    await progress.CompleteAsync($"⛔ Drop stopped · {reason}", CancellationToken.None);
                    return;
                }

                ideaId = shaped.IdeaId;
                await notifier.SendAsync(shaped.Html, cancellationToken);
            }
            finally
            {
                OperationGates.Ideation.Release();
            }

            if (ideaId is null)
            {
                return;
            }

            // Checkpoint: restart after this point resumes directly at research.
            using var checkpointScope = scopeFactory.CreateScope();
            await checkpointScope.ServiceProvider.GetRequiredService<JobService>()
                .CheckpointAsync(job.Id, payload with { IdeaId = ideaId }, cancellationToken);
        }

        var result = await researchCoordinator.RunAsync(ideaId.Value, progress, wait: true, cancellationToken);
        if (result is null)
        {
            return;
        }

        if (result.StoppedReason is { } stopped)
        {
            await progress.CompleteAsync($"⛔ research stopped: {stopped}", CancellationToken.None);
            return;
        }

        await progress.CompleteAsync(
            $"✅ done: {result.Verdict?.ToUpperInvariant()} · report below", CancellationToken.None);
        await notifier.SendAsync(result.Html, cancellationToken);
    }

    private async Task ExecuteResearchAsync(JobEntity job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ResearchJobPayload>(job.PayloadJson, LlmJson.Options)
            ?? throw new InvalidOperationException("research payload unreadable");

        var progress = await progressNotifier.StartAsync(
            $"🔎 Research #{payload.IdeaId} (job #{job.Id}) · preparing…", cancellationToken);

        var result = await researchCoordinator.RunAsync(payload.IdeaId, progress, wait: true, cancellationToken);
        if (result is null)
        {
            return;
        }

        if (result.StoppedReason is { } reason)
        {
            await progress.CompleteAsync($"⛔ stopped · {reason}", CancellationToken.None);
            return;
        }

        await progress.CompleteAsync(
            $"✅ done · {result.Verdict?.ToUpperInvariant()} · report below", CancellationToken.None);
        await notifier.SendAsync(result.Html, cancellationToken);
    }

    private async Task FinishAsync(long jobId, string? error, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<JobService>()
            .CompleteAsync(jobId, error, cancellationToken);
    }

    private async Task RequeueAsync(long jobId, string error, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<JobService>()
            .RequeueForRetryAsync(jobId, error, cancellationToken);
    }
}
