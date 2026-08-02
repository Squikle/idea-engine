using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Maintenance;

/// <summary>Bound from configuration section <c>IdeaEngine:Retention</c>.</summary>
public sealed class RetentionOptions
{
    /// <summary>Reddit content copies are stripped after this many days (ADR-0004 posture:
    /// without Data API access we cannot verify deletions, so we minimize retention).</summary>
    public int RedditContentDays { get; set; } = 30;

    public int PipelineRunsDays { get; set; } = 90;
}

/// <summary>
/// Compliance + housekeeping. Reddit-sourced content text is stripped (title placeholder,
/// body/comments/payload nulled) while derived signals and the reference URL survive.
/// </summary>
public sealed class RetentionService(
    IdeaEngineDbContext db,
    TimeProvider timeProvider,
    IOptions<RetentionOptions> retentionOptions,
    ILogger<RetentionService> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var options = retentionOptions.Value;
        var now = timeProvider.GetUtcNow();

        var redditCutoff = now.AddDays(-options.RedditContentDays);
        var stripped = await db.RawItems
            .Where(r => r.Source == SourceKind.RedditRss
                && r.FetchedAt < redditCutoff
                && r.Body != null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.Title, "[content expired per retention policy]")
                    .SetProperty(r => r.Body, (string?)null)
                    .SetProperty(r => r.CommentsJson, (string?)null)
                    .SetProperty(r => r.RawPayloadJson, (string?)null),
                cancellationToken);

        var runsCutoff = now.AddDays(-options.PipelineRunsDays);
        var prunedRuns = await db.PipelineRuns
            .Where(r => r.StartedAt < runsCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (stripped > 0 || prunedRuns > 0)
        {
            logger.LogInformation(
                "Retention: stripped {Stripped} Reddit items, pruned {Runs} old pipeline runs",
                stripped, prunedRuns);
        }
    }
}
