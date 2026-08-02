using System.Globalization;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Infrastructure.Autopilot;
using IdeaEngine.Infrastructure.Ideation;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Worker;

/// <summary>
/// The autonomy loop: daily scheduled ideation (with auto-research of the best fresh
/// candidates) and the daily digest - both at local wall-clock times. On startup, if no
/// product ideas exist at all, it bootstraps an ideation run immediately so the owner
/// sees real output without touching anything.
/// </summary>
internal sealed class AutopilotHostedService(
    IServiceScopeFactory scopeFactory,
    IProgressNotifier progressNotifier,
    IStatusTracker statusTracker,
    INotifier notifier,
    TimeProvider timeProvider,
    TimeZoneInfo timeZone,
    IOptions<AutopilotOptions> autopilotOptions,
    ILogger<AutopilotHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = autopilotOptions.Value;
        if (!config.Enabled)
        {
            logger.LogInformation("Autopilot disabled by configuration");
            return;
        }

        var ideationTime = ParseTime(config.IdeationTime, new TimeOnly(10, 0));
        var digestTime = ParseTime(config.DigestTime, new TimeOnly(21, 0));

        // Let ingestion/triage settle after startup, then bootstrap if needed.
        await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
        await BootstrapIfNoIdeasAsync(config, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            var nextIdeation = Scheduling.NextOccurrence(ideationTime, timeZone, now);
            var nextDigest = Scheduling.NextOccurrence(digestTime, timeZone, now);
            var runIdeation = nextIdeation <= nextDigest;
            var nextAt = runIdeation ? nextIdeation : nextDigest;
            await statusTracker.ScheduleAsync(Tracks.Ideate, nextIdeation, stoppingToken);
            await statusTracker.ScheduleAsync(Tracks.DigestTrack, nextDigest, stoppingToken);

            logger.LogInformation(
                "Autopilot next: {Kind} at {Local} {Zone}",
                runIdeation ? "ideation" : "digest",
                TimeZoneInfo.ConvertTime(nextAt, timeZone).ToString("HH:mm", CultureInfo.InvariantCulture),
                Scheduling.ZoneLabel(timeZone));

            var wait = nextAt - now;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, stoppingToken);
            }

            try
            {
                if (runIdeation)
                {
                    await RunIdeationWithResearchAsync(config, stoppingToken);
                }
                else
                {
                    await RunDigestAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Autopilot {Kind} run crashed", runIdeation ? "ideation" : "digest");
            }
        }
    }

    private async Task BootstrapIfNoIdeasAsync(AutopilotOptions config, CancellationToken cancellationToken)
    {
        try
        {
            bool hasProductIdeas;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
                hasProductIdeas = await db.Ideas.AnyAsync(i => i.Category != "meta", cancellationToken);
            }

            if (hasProductIdeas)
            {
                return;
            }

            logger.LogInformation("Autopilot bootstrap: no product ideas exist yet, running first ideation");
            await notifier.SendAsync(
                "Autopilot: no product ideas exist yet — running the first ideation now.", cancellationToken);
            await RunIdeationWithResearchAsync(config, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Autopilot bootstrap failed");
        }
    }

    private async Task RunIdeationWithResearchAsync(AutopilotOptions config, CancellationToken cancellationToken)
    {
        if (!await OperationGates.Ideation.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            logger.LogInformation("Autopilot ideation skipped: a manual run is in progress");
            return;
        }

        IdeationBatchResult result;
        try
        {
            var progress = await progressNotifier.StartAsync("Autopilot · ideation starting…", cancellationToken);

            using var scope = scopeFactory.CreateScope();
            var ideation = scope.ServiceProvider.GetRequiredService<IdeationService>();
            result = await ideation.RunProductSessionsAsync(config.SessionsPerDay, progress, cancellationToken);

            await progress.CompleteAsync(
                $"Autopilot ideation done · {result.Advanced} live · {result.Killed} killed · " +
                $"${result.CostUsd.ToString("F2", CultureInfo.InvariantCulture)}",
                cancellationToken);
            await notifier.SendAsync(IdeationFormatting.BuildResultsHtml(result), cancellationToken);
        }
        finally
        {
            OperationGates.Ideation.Release();
        }

        if (config.AutoResearchTop <= 0 || result.Advanced == 0)
        {
            return;
        }

        await AutoResearchAsync(config, cancellationToken);
    }

    private async Task AutoResearchAsync(AutopilotOptions config, CancellationToken cancellationToken)
    {
        List<(long Id, string Title, double Rating)> rated;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
            var since = timeProvider.GetUtcNow().AddHours(-48);
            var researchedIds = db.ResearchReports.Select(r => r.IdeaId);
            var candidates = await db.Ideas
                .Where(i => i.Status == "candidate" && i.Category != "meta" && !i.Verified
                    && i.CreatedAt >= since && !researchedIds.Contains(i.Id))
                .ToListAsync(cancellationToken);

            rated = [.. candidates.Select(i => (i.Id, i.Title, IdeaJson.ComputeRating(i)))];
        }

        if (rated.Count == 0)
        {
            return;
        }

        // Individual judgment: everyone above the absolute floor gets a durable job.
        var eligible = rated
            .Where(x => x.Rating >= config.MinRatingForResearch)
            .OrderByDescending(x => x.Rating)
            .ToList();
        var queued = eligible.Take(Math.Max(0, config.AutoResearchTop)).ToList();
        var overflow = eligible.Skip(queued.Count).ToList();
        var belowFloor = rated.Where(x => x.Rating < config.MinRatingForResearch).ToList();

        var builder = new System.Text.StringBuilder("🔬 <b>Auto-research</b>\n");
        if (queued.Count > 0)
        {
            using var scope = scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Jobs.JobService>();
            foreach (var (id, title, rating) in queued)
            {
                var (jobId, position) = await jobs.EnqueueAsync(
                    "research", new IdeaEngine.Infrastructure.Jobs.ResearchJobPayload(id), null, cancellationToken);
                builder.Append("• queued job #").Append(jobId).Append(" · #").Append(id)
                    .Append(" <i>(≈").Append((rating * 100).ToString("F0")).Append("%)</i> ")
                    .Append(System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(title, 45)))
                    .Append('\n');
                _ = position;
            }
        }

        foreach (var (id, title, rating) in belowFloor)
        {
            builder.Append("⏭ skipped #").Append(id).Append(" <i>(≈")
                .Append((rating * 100).ToString("F0")).Append("% &lt; ")
                .Append((config.MinRatingForResearch * 100).ToString("F0"))
                .Append("% floor)</i> ")
                .Append(System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(title, 40)))
                .Append(" — /research ").Append(id).Append(" to force\n");
        }

        foreach (var (id, title, _) in overflow)
        {
            builder.Append("⏳ deferred #").Append(id).Append(" (daily auto-limit) ")
                .Append(System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(title, 40)))
                .Append(" — /research ").Append(id).Append('\n');
        }

        builder.Append("<i>every idea judged individually · floor is absolute, not relative · /queue to watch</i>");
        await notifier.SendAsync(builder.ToString(), cancellationToken);
    }

    private async Task RunDigestAsync(CancellationToken cancellationToken)
    {
        await statusTracker.BeginAsync(Tracks.DigestTrack, "building…", cancellationToken);
        using var scope = scopeFactory.CreateScope();
        var digest = scope.ServiceProvider.GetRequiredService<DigestService>();
        await notifier.SendAsync(await digest.BuildAsync(cancellationToken), cancellationToken);
        await statusTracker.EndAsync(Tracks.DigestTrack, "sent", CancellationToken.None);
    }

    private static TimeOnly ParseTime(string value, TimeOnly fallback) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;
}
