using System.Globalization;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Infrastructure.Autopilot;
using IdeaEngine.Infrastructure.Ideation;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Reporting;
using IdeaEngine.Infrastructure.Research;
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
    ResearchCoordinator researchCoordinator,
    IProgressNotifier progressNotifier,
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
        List<(long Id, double Rating)> ranked;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
            var since = timeProvider.GetUtcNow().AddHours(-24);
            var candidates = await db.Ideas
                .Where(i => i.Status == "candidate" && i.Category != "meta" && i.CreatedAt >= since)
                .ToListAsync(cancellationToken);

            ranked = candidates
                .Select(i => (i.Id, Rating: IdeaJson.ComputeRating(i)))
                .Where(x => x.Rating >= config.MinRatingForResearch)
                .OrderByDescending(x => x.Rating)
                .Take(config.AutoResearchTop)
                .ToList();
        }

        if (ranked.Count == 0)
        {
            await notifier.SendAsync(
                $"Autopilot: no fresh candidate rated ≥ {config.MinRatingForResearch:F2} — " +
                "not spending research money on weak ideas today.",
                cancellationToken);
            return;
        }

        foreach (var (ideaId, rating) in ranked)
        {
            var progress = await progressNotifier.StartAsync(
                $"Autopilot · researching #{ideaId} (r{rating:F2})…", cancellationToken);

            var result = await researchCoordinator.RunAsync(ideaId, progress, wait: true, cancellationToken);
            if (result is null)
            {
                continue; // unreachable with wait: true, defensive
            }

            if (result.StoppedReason is { } reason)
            {
                await progress.CompleteAsync(
                    $"Autopilot research #{ideaId} stopped · {System.Net.WebUtility.HtmlEncode(reason)}",
                    cancellationToken);
                continue;
            }

            await progress.CompleteAsync(
                $"Autopilot research #{ideaId} done · {result.Verdict?.ToUpperInvariant()} · report below",
                cancellationToken);
            await notifier.SendAsync(result.Html, cancellationToken);
        }
    }

    private async Task RunDigestAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var digest = scope.ServiceProvider.GetRequiredService<DigestService>();
        await notifier.SendAsync(await digest.BuildAsync(cancellationToken), cancellationToken);
    }

    private static TimeOnly ParseTime(string value, TimeOnly fallback) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;
}
