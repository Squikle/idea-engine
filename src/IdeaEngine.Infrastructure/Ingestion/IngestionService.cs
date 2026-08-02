using System.Diagnostics;
using System.Text.Json;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Notifications;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Ingestion;

/// <summary>
/// One ingestion cycle: every registered source adapter is fetched, items are deduplicated
/// (exact external id, then content hash across sources) and stored as <see cref="ItemStatus.New"/>.
/// Each source gets its own pipeline_runs row; one failing source never stops the others.
/// The cycle ends with an owner notification (Telegram) summarizing what arrived.
/// </summary>
public sealed class IngestionService(
    IdeaEngineDbContext db,
    IEnumerable<ISourceAdapter> adapters,
    INotifier notifier,
    IStatusBoard statusBoard,
    TimeProvider timeProvider,
    IOptions<IngestionOptions> ingestionOptions,
    ILogger<IngestionService> logger)
{
    // Cycle-level status (Collecting/Idle) is owned by IngestionCoordinator;
    // this service reports per-source progress only.
    private const int HighlightsPerSource = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IngestionCycleReport> RunAsync(
        SourceKind? only, CancellationToken cancellationToken)
    {
        var config = ingestionOptions.Value;
        var selected = adapters.Where(a => only is null || a.Kind == only).ToList();
        logger.LogInformation("Ingestion cycle starting ({AdapterCount} sources)", selected.Count);

        var results = new List<SourceIngestResult>();
        foreach (var adapter in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await IngestSourceAsync(adapter, config, cancellationToken));
        }

        var report = new IngestionCycleReport(timeProvider.GetUtcNow(), results);

        if (config.NotifyEveryCycle || report.TotalStored > 0 || report.TotalErrors > 0)
        {
            await notifier.SendAsync(IngestionReportFormatter.Format(report), cancellationToken);
        }

        logger.LogInformation(
            "Ingestion cycle finished: {Stored} new items across {Sources} sources",
            report.TotalStored, results.Count);

        return report;
    }

    private async Task<SourceIngestResult> IngestSourceAsync(
        ISourceAdapter adapter, IngestionOptions config, CancellationToken cancellationToken)
    {
        var run = new PipelineRunEntity
        {
            Stage = $"ingest:{adapter.Kind}",
            StartedAt = timeProvider.GetUtcNow(),
        };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var fetched = 0;
        var stored = 0;
        var duplicates = 0;
        var errors = 0;
        var highlights = new List<IngestedHighlight>();

        try
        {
            logger.LogInformation("→ {Source}: fetching (max {Max})…", adapter.Kind, config.MaxItemsPerSource);
            await statusBoard.UpdateAsync("Collecting", $"{adapter.Kind}…", null, cancellationToken);

            var items = new List<RawItem>();
            await foreach (var item in adapter.FetchAsync(
                new SourceFetchOptions { MaxItems = config.MaxItemsPerSource }, cancellationToken))
            {
                fetched++;
                items.Add(item);
            }

            (stored, duplicates, highlights) = await StoreBatchAsync(items, config, cancellationToken);
            await statusBoard.UpdateAsync(
                "Collecting", $"{adapter.Kind}: +{stored} new", null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors++;
            run.Notes = Truncate(ex.Message, 2000);
            logger.LogError(ex, "✗ {Source}: ingestion failed", adapter.Kind);
        }
        finally
        {
            run.FinishedAt = timeProvider.GetUtcNow();
            run.ItemsIn = fetched;
            run.ItemsOut = stored;
            run.Errors = errors;
            await db.SaveChangesAsync(CancellationToken.None);

            logger.LogInformation(
                "✓ {Source}: fetched {Fetched}, stored {Stored} new, skipped {Duplicates} known, {Errors} errors in {Elapsed:F1}s",
                adapter.Kind, fetched, stored, duplicates, errors, stopwatch.Elapsed.TotalSeconds);
        }

        return new SourceIngestResult(
            adapter.Kind, fetched, stored, duplicates, errors, stopwatch.Elapsed.TotalSeconds, highlights);
    }

    private async Task<(int Stored, int Duplicates, List<IngestedHighlight> Highlights)> StoreBatchAsync(
        List<RawItem> items, IngestionOptions config, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return (0, 0, []);
        }

        var source = items[0].Source;
        var externalIds = items.Select(i => i.ExternalId).ToList();
        var knownIds = (await db.RawItems
                .Where(r => r.Source == source && externalIds.Contains(r.ExternalId))
                .Select(r => r.ExternalId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var hashed = items
            .Where(i => !knownIds.Contains(i.ExternalId))
            .Select(i => (Item: i, Hash: ContentHasher.Compute(i.Title, i.Body)))
            .ToList();

        var candidateHashes = hashed.Select(p => p.Hash).ToList();
        var knownHashes = (await db.RawItems
                .Where(r => candidateHashes.Contains(r.ContentHash))
                .Select(r => r.ContentHash)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var storedItems = new List<RawItem>();
        var duplicates = items.Count - hashed.Count;

        foreach (var (item, hash) in hashed)
        {
            if (!knownHashes.Add(hash))
            {
                duplicates++;
                logger.LogDebug("  = cross-duplicate skipped: {Title}", Truncate(item.Title, 90));
                continue;
            }

            db.RawItems.Add(new RawItemEntity
            {
                Source = item.Source,
                ExternalId = item.ExternalId,
                Title = Truncate(item.Title, 2000),
                Body = item.Body,
                Url = item.Url is { Length: > 2000 } ? item.Url[..2000] : item.Url,
                Author = TruncateOrNull(item.Author, 250),
                Community = TruncateOrNull(item.Community, 120),
                Score = item.Score,
                CommentCount = item.CommentCount,
                ContentHash = hash,
                CommentsJson = item.Comments.Count > 0
                    ? JsonSerializer.Serialize(item.Comments, JsonOptions)
                    : null,
                Status = ItemStatus.New,
                CreatedAt = item.CreatedAt,
                FetchedAt = item.FetchedAt,
            });
            storedItems.Add(item);

            if (config.VerboseItemLogging)
            {
                logger.LogInformation(
                    "  + [{Community}] {Score}pts {Comments}c | {Title}",
                    item.Community, item.Score, item.CommentCount, Truncate(item.Title, 90));
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var highlights = storedItems
            .OrderByDescending(i => i.Score)
            .Take(HighlightsPerSource)
            .Select(i => new IngestedHighlight(i.Title, i.Url, i.Score, i.CommentCount))
            .ToList();

        return (storedItems.Count, duplicates, highlights);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string? TruncateOrNull(string? value, int max) =>
        value is null ? null : Truncate(value, max);
}
