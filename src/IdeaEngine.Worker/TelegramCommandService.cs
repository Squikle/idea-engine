using System.Globalization;
using System.Text;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ideation;
using IdeaEngine.Infrastructure.Ingestion;
using IdeaEngine.Infrastructure.Notifications;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Triage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace IdeaEngine.Worker;

/// <summary>
/// Long-polling command listener. Only the admin chat is honored; everything else is
/// ignored silently. Commands: /status /signals /top /costs /collect /analyze /config /help.
/// </summary>
internal sealed class TelegramCommandService(
    IServiceProvider serviceProvider,
    IServiceScopeFactory scopeFactory,
    IngestionCoordinator coordinator,
    TriageCoordinator triageCoordinator,
    INotifier notifier,
    TimeProvider timeProvider,
    IOptions<IngestionOptions> ingestionOptions,
    ILogger<TelegramCommandService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _ideateGate = new(1, 1);
    private ITelegramBotClient? _bot;
    private long _adminChatId;
    private DateTimeOffset _startedAt;

    public override void Dispose()
    {
        _ideateGate.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var telegram = serviceProvider.GetService<TelegramOptions>();
        _bot = serviceProvider.GetService<ITelegramBotClient>();
        if (_bot is null || telegram is not { IsConfigured: true })
        {
            logger.LogInformation("Telegram not configured; command listener disabled");
            return;
        }

        _adminChatId = telegram.AdminChatId!.Value;
        _startedAt = timeProvider.GetUtcNow();

        await RegisterCommandMenuAsync(stoppingToken);
        var offset = await SkipBacklogAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _bot.GetUpdates(
                    offset: offset,
                    timeout: 25,
                    allowedUpdates: [UpdateType.Message],
                    cancellationToken: stoppingToken);

                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    await HandleUpdateAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telegram polling error; retrying in 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RegisterCommandMenuAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _bot!.SetMyCommands(
            [
                new BotCommand { Command = "status", Description = "pipeline state, counts, next run" },
                new BotCommand { Command = "best", Description = "top-valued signals, 7 days" },
                new BotCommand { Command = "signals", Description = "latest extracted signals" },
                new BotCommand { Command = "top", Description = "top items from the last 24h" },
                new BotCommand { Command = "costs", Description = "AI spend, last 7 days" },
                new BotCommand { Command = "collect", Description = "run a cycle now (optionally one source)" },
                new BotCommand { Command = "analyze", Description = "run AI triage on the queue now" },
                new BotCommand { Command = "ideate", Description = "AI builder-vs-skeptic idea sessions" },
                new BotCommand { Command = "ideas", Description = "recent ideas (live and killed)" },
                new BotCommand { Command = "advise", Description = "AI reviews our own pipeline for gaps" },
                new BotCommand { Command = "config", Description = "current configuration" },
                new BotCommand { Command = "help", Description = "command list" },
            ], cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SetMyCommands failed (non-fatal)");
        }
    }

    private async Task<int> SkipBacklogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pending = await _bot!.GetUpdates(offset: -1, limit: 1, cancellationToken: cancellationToken);
            return pending.Length > 0 ? pending[^1].Id + 1 : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Skipping Telegram backlog failed; starting from 0");
            return 0;
        }
    }

    private async Task HandleUpdateAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { Text: { } text } message || message.Chat.Id != _adminChatId)
        {
            return;
        }

        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant().TrimStart('/');
        var argument = parts.Length > 1 ? parts[1].Trim() : null;

        try
        {
            var reply = command switch
            {
                "status" => await BuildStatusAsync(cancellationToken),
                "top" => await BuildTopAsync(cancellationToken),
                "signals" => await BuildSignalsAsync(cancellationToken),
                "best" => await BuildBestAsync(argument, cancellationToken),
                "idea" => await BuildIdeaDetailAsync(argument, cancellationToken),
                "costs" => await BuildCostsAsync(cancellationToken),
                "collect" => StartCollect(argument),
                "analyze" => StartAnalyze(),
                "ideate" => StartIdeate(argument),
                "advise" => StartAdvise(),
                "ideas" => await BuildIdeasAsync(cancellationToken),
                "config" => BuildConfig(),
                "help" or "start" => BuildHelp(),
                _ => $"Unknown command: /{command} — try /help",
            };

            await ReplyAsync(reply, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command /{Command} failed", command);
            await ReplyAsync($"/{command} failed: {ex.GetType().Name}", cancellationToken);
        }
    }

    private async Task<string> BuildStatusAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var now = timeProvider.GetUtcNow();

        var bySource = await db.RawItems
            .GroupBy(r => r.Source)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var byStatus = await db.RawItems
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var lastRuns = await db.PipelineRuns
            .OrderByDescending(r => r.Id)
            .Take(5)
            .ToListAsync(cancellationToken);

        var builder = new StringBuilder();
        builder.Append("<b>Status</b>\n");
        builder.Append(coordinator.IsRunning ? "Collecting now\n" : "Idle\n");
        if (coordinator.NextCycleAt is { } next)
        {
            builder.Append("Next cycle: ").Append(next.ToString("HH:mm", CultureInfo.InvariantCulture))
                .Append(" UTC (in ").Append(FormatWait(next - now)).Append(")\n");
        }

        builder.Append("Uptime: ").Append(FormatWait(now - _startedAt)).Append('\n');

        builder.Append("\n<b>Items by source</b>\n");
        foreach (var row in bySource.OrderByDescending(r => r.Count))
        {
            builder.Append(row.Source).Append(": ").Append(row.Count).Append('\n');
        }

        builder.Append("\n<b>By stage</b>\n");
        foreach (var row in byStatus.OrderBy(r => r.Status))
        {
            builder.Append(row.Status).Append(": ").Append(row.Count).Append('\n');
        }

        builder.Append("\n<b>Recent runs</b>\n");
        foreach (var run in lastRuns)
        {
            var took = run.FinishedAt is { } finished ? (finished - run.StartedAt).TotalSeconds : 0;
            builder.Append(run.Stage).Append(": ").Append(run.ItemsOut).Append('/').Append(run.ItemsIn)
                .Append(run.Errors > 0 ? $" ({run.Errors} err)" : string.Empty)
                .Append(" · ").Append(took.ToString("F0", CultureInfo.InvariantCulture)).Append("s\n");
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string> BuildTopAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var since = timeProvider.GetUtcNow().AddHours(-24);

        var top = await db.RawItems
            .Where(r => r.FetchedAt >= since)
            .OrderByDescending(r => r.Score)
            .Take(10)
            .Select(r => new { r.Source, r.Title, r.Url, r.Score, r.CommentCount })
            .ToListAsync(cancellationToken);

        if (top.Count == 0)
        {
            return "Nothing collected in the last 24h.";
        }

        var builder = new StringBuilder("<b>Top of the last 24h</b>\n");
        var rank = 1;
        foreach (var item in top)
        {
            var title = System.Net.WebUtility.HtmlEncode(
                item.Title.Length > 80 ? item.Title[..79] + "…" : item.Title);

            builder.Append(rank++).Append(". ");
            if (item.Url is { Length: > 0 })
            {
                builder.Append("<a href=\"").Append(item.Url).Append("\">").Append(title).Append("</a>");
            }
            else
            {
                builder.Append(title);
            }

            builder.Append(" — ").Append(item.Score).Append(" pts [").Append(item.Source).Append("]\n");
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string> BuildSignalsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();

        var signals = await db.Signals
            .OrderByDescending(s => s.Id)
            .Take(12)
            .Select(s => new
            {
                s.Kind,
                s.Summary,
                s.CommercialSentiment,
                s.Confidence,
                s.RawItem!.Url,
                s.RawItem.Source,
            })
            .ToListAsync(cancellationToken);

        if (signals.Count == 0)
        {
            return "No signals extracted yet — triage may still be draining the queue (/status).";
        }

        var builder = new StringBuilder("<b>Latest signals</b>\n");
        foreach (var signal in signals)
        {
            builder.Append("• <b>").Append(signal.Kind).Append("</b> ")
                .Append(System.Net.WebUtility.HtmlEncode(signal.Summary));

            builder.Append(" <i>(").Append(signal.CommercialSentiment.Replace('_', ' '))
                .Append(", ").Append((signal.Confidence * 100).ToString("F0", CultureInfo.InvariantCulture))
                .Append("%)</i>");

            if (signal.Url is { Length: > 0 })
            {
                builder.Append(" <a href=\"").Append(signal.Url).Append("\">[").Append(signal.Source).Append("]</a>");
            }

            builder.Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string> BuildCostsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var weekAgo = today.AddDays(-7);

        var rows = await db.AiLedger
            .Where(e => e.Day >= weekAgo)
            .GroupBy(e => new { e.Day, e.Stage, e.Model })
            .Select(g => new
            {
                g.Key.Day,
                g.Key.Stage,
                g.Key.Model,
                Cost = g.Sum(e => e.CostUsd),
                TokensIn = g.Sum(e => e.TokensIn),
                TokensOut = g.Sum(e => e.TokensOut),
                Calls = g.Count(),
            })
            .OrderByDescending(r => r.Day)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return "No AI spend recorded yet.";
        }

        var builder = new StringBuilder("<b>AI costs (7 days)</b>\n");
        foreach (var row in rows)
        {
            builder.Append(row.Day == today ? "today" : row.Day.ToString("dd MMM", CultureInfo.InvariantCulture))
                .Append(" · ").Append(row.Stage)
                .Append(" · ").Append(row.Calls).Append(" calls · ")
                .Append((row.TokensIn / 1000.0).ToString("F0", CultureInfo.InvariantCulture)).Append("k in / ")
                .Append((row.TokensOut / 1000.0).ToString("F0", CultureInfo.InvariantCulture)).Append("k out · $")
                .Append(row.Cost.ToString("F4", CultureInfo.InvariantCulture))
                .Append('\n');
        }

        builder.Append("\nTotal: $")
            .Append(rows.Sum(r => r.Cost).ToString("F4", CultureInfo.InvariantCulture));

        return builder.ToString();
    }

    private string StartCollect(string? argument)
    {
        SourceKind? only = null;
        if (argument is not null)
        {
            if (!SourceKindParser.TryParse(argument, out var kind))
            {
                return $"Unknown source '{argument}'. Try: hn, 4chan, bluesky, lemmy, reddit — or plain /collect for all.";
            }

            only = kind;
        }

        if (coordinator.IsRunning)
        {
            return "A cycle is already running — results will arrive when it finishes.";
        }

        // Fire and forget: completion announces itself via the cycle report message.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await coordinator.TryRunAsync(only, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Manual collect failed");
                }
            },
            CancellationToken.None);

        return only is { } k ? $"Collecting {k} now…" : "Collecting all sources now…";
    }

    private string StartAnalyze()
    {
        if (triageCoordinator.IsRunning)
        {
            return "Analysis is already running — /status to watch.";
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var result = await triageCoordinator.TryDrainAsync(CancellationToken.None);
                    if (result is { Analyzed: > 0 } or { Capped: true })
                    {
                        var summary = result.Capped
                            ? $"Analysis stopped at daily cap: {result.Analyzed} items, +{result.SignalsFound} signals, ${result.CostUsd:F4} ({result.Queued} left)"
                            : $"Analysis done: {result.Analyzed} items, +{result.SignalsFound} signals, ${result.CostUsd:F4}";
                        await notifier.SendAsync(summary, CancellationToken.None);
                    }
                    else if (result is { Analyzed: 0 })
                    {
                        await notifier.SendAsync("Nothing queued for analysis.", CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Manual analysis failed");
                }
            },
            CancellationToken.None);

        return "Analyzing the queue now…";
    }

    private string StartIdeate(string? argument)
    {
        var count = 1;
        if (argument is not null && (!int.TryParse(argument, out count) || count < 1))
        {
            return "Usage: /ideate [1-10]";
        }

        if (!_ideateGate.Wait(0))
        {
            return "Ideation is already running — results will arrive when it finishes.";
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var ideation = scope.ServiceProvider.GetRequiredService<IdeationService>();
                    var result = await ideation.RunProductSessionsAsync(count, CancellationToken.None);

                    var builder = new StringBuilder("<b>Ideation finished</b>\n");
                    foreach (var line in result.Lines)
                    {
                        builder.Append(System.Net.WebUtility.HtmlEncode(line)).Append('\n');
                    }

                    builder.Append('\n').Append(result.Advanced).Append(" advanced · ")
                        .Append(result.Killed).Append(" killed · ")
                        .Append(result.Errors).Append(" errors · $")
                        .Append(result.CostUsd.ToString("F4", CultureInfo.InvariantCulture));
                    if (result.StoppedReason is { } reason)
                    {
                        builder.Append("\nStopped: ").Append(System.Net.WebUtility.HtmlEncode(reason));
                    }

                    await notifier.SendAsync(builder.ToString(), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Ideation batch failed");
                    await notifier.SendAsync("Ideation crashed — check logs.", CancellationToken.None);
                }
                finally
                {
                    _ideateGate.Release();
                }
            },
            CancellationToken.None);

        return $"Running {Math.Clamp(count, 1, 10)} ideation session(s)… builder vs skeptic, grounded in your signals.";
    }

    private string StartAdvise()
    {
        if (!_ideateGate.Wait(0))
        {
            return "Ideation is already running — try again when it finishes.";
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var ideation = scope.ServiceProvider.GetRequiredService<IdeationService>();
                    var result = await ideation.RunMetaSessionAsync(CancellationToken.None);
                    var message = result.StoppedReason is { } reason
                        ? $"Advise stopped: {System.Net.WebUtility.HtmlEncode(reason)}"
                        : result.Html;
                    await notifier.SendAsync(message, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Meta advice session failed");
                    await notifier.SendAsync("Advise crashed — check logs.", CancellationToken.None);
                }
                finally
                {
                    _ideateGate.Release();
                }
            },
            CancellationToken.None);

        return "Asking the advisor what our pipeline is missing…";
    }

    private async Task<string> BuildIdeasAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();

        var ideas = await db.Ideas
            .OrderByDescending(i => i.Id)
            .Take(20)
            .ToListAsync(cancellationToken);

        if (ideas.Count == 0)
        {
            return "No ideas yet — run /ideate to spin up sessions.";
        }

        var rated = ideas
            .Select(i => new { Idea = i, Rating = ComputeRating(i) })
            .OrderByDescending(x => x.Idea.Status == "candidate")
            .ThenByDescending(x => x.Rating)
            .ThenByDescending(x => x.Idea.Id)
            .Take(12);

        var builder = new StringBuilder("<b>Ideas</b> (candidates first, by rating)\n");
        foreach (var entry in rated)
        {
            var marker = entry.Idea.Status == "candidate" ? "LIVE" : "dead";
            builder.Append("• #").Append(entry.Idea.Id)
                .Append(" r").Append(entry.Rating.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" [").Append(marker).Append("] [").Append(entry.Idea.Category)
                .Append("/e").Append(entry.Idea.EffortScale).Append("] ")
                .Append(System.Net.WebUtility.HtmlEncode(entry.Idea.Title)).Append('\n');
        }

        builder.Append("\n/idea &lt;id&gt; for the full trace");
        return builder.ToString();
    }

    private async Task<string> BuildBestAsync(string? argument, CancellationToken cancellationToken)
    {
        var count = 8;
        if (argument is not null && (!int.TryParse(argument, out count) || count < 1 || count > 15))
        {
            return "Usage: /best [1-15]";
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var since = timeProvider.GetUtcNow().AddDays(-7);

        var signals = await db.Signals
            .Where(s => s.CreatedAt >= since)
            .OrderByDescending(s => s.Confidence)
            .Take(150)
            .Select(s => new
            {
                s.Id,
                s.Kind,
                s.Summary,
                s.CommercialSentiment,
                s.Confidence,
                s.Novelty,
                s.RawItem!.Url,
                s.RawItem.Source,
            })
            .ToListAsync(cancellationToken);

        if (signals.Count == 0)
        {
            return "No signals in the last 7 days — /collect then /analyze.";
        }

        // Which signals already fed ideas (evidence trace).
        var ideas = await db.Ideas
            .OrderByDescending(i => i.Id)
            .Take(100)
            .Select(i => new { i.Id, i.Status, i.EvidenceJson })
            .ToListAsync(cancellationToken);

        var signalToIdea = new Dictionary<long, (long IdeaId, string Status)>();
        foreach (var idea in ideas)
        {
            foreach (var signalId in ParseEvidence(idea.EvidenceJson))
            {
                signalToIdea.TryAdd(signalId, (idea.Id, idea.Status));
            }
        }

        var top = signals
            .Select(s => new
            {
                s.Id,
                s.Kind,
                s.Summary,
                s.Url,
                s.Source,
                Value = SignalScoring.Value(s.Confidence, s.Novelty, s.CommercialSentiment),
            })
            .OrderByDescending(s => s.Value)
            .Take(count)
            .ToList();

        var builder = new StringBuilder("<b>Best signals · 7 days</b>\n");
        var rank = 1;
        foreach (var signal in top)
        {
            builder.Append(rank++).Append(". v")
                .Append(signal.Value.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" [").Append(signal.Kind).Append("] ")
                .Append(System.Net.WebUtility.HtmlEncode(
                    signal.Summary.Length > 100 ? signal.Summary[..99] + "…" : signal.Summary));

            if (signal.Url is { Length: > 0 })
            {
                builder.Append(" <a href=\"").Append(signal.Url).Append("\">[").Append(signal.Source).Append("]</a>");
            }

            if (signalToIdea.TryGetValue(signal.Id, out var idea))
            {
                builder.Append(" → #").Append(idea.IdeaId)
                    .Append(idea.Status == "candidate" ? " LIVE" : " dead");
            }

            builder.Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string> BuildIdeaDetailAsync(string? argument, CancellationToken cancellationToken)
    {
        if (!long.TryParse(argument, out var ideaId))
        {
            return "Usage: /idea 5 (ids are shown by /ideas)";
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();

        var idea = await db.Ideas.FindAsync([ideaId], cancellationToken);
        if (idea is null)
        {
            return $"No idea #{ideaId}.";
        }

        var skeptic = idea.SkepticJson is null ? null : LlmJson.TryParse<SkepticReview>(idea.SkepticJson);
        var rating = ComputeRating(idea);

        var builder = new StringBuilder();
        builder.Append("<b>#").Append(idea.Id).Append(" · ")
            .Append(System.Net.WebUtility.HtmlEncode(idea.Title)).Append("</b>\n")
            .Append(idea.Status == "candidate" ? "LIVE" : "dead").Append(" · ")
            .Append(idea.Category).Append(" · effort ").Append(idea.EffortScale)
            .Append(" · rating ").Append(rating.ToString("F2", CultureInfo.InvariantCulture)).Append("\n\n")
            .Append(System.Net.WebUtility.HtmlEncode(idea.Thesis)).Append('\n');

        AppendLine(builder, "Target", idea.TargetUser);
        AppendLine(builder, "Monetization", idea.Monetization);
        AppendLine(builder, "Distribution", idea.DistributionNote);

        if (skeptic is not null)
        {
            builder.Append("\n<b>Skeptic</b> (").Append(skeptic.Verdict ?? "?")
                .Append(", conf ").Append(skeptic.Confidence.ToString("F2", CultureInfo.InvariantCulture)).Append(")\n");

            foreach (var reason in (skeptic.KillReasons ?? []).Concat(skeptic.Weaknesses ?? []).Take(3))
            {
                builder.Append("– ").Append(System.Net.WebUtility.HtmlEncode(reason)).Append('\n');
            }

            if (skeptic.ResearchQuestions is { Count: > 0 } questions)
            {
                builder.Append("<b>To research</b>\n");
                foreach (var question in questions.Take(5))
                {
                    builder.Append("? ").Append(System.Net.WebUtility.HtmlEncode(question)).Append('\n');
                }
            }
        }

        var evidenceIds = ParseEvidence(idea.EvidenceJson).Take(6).ToList();
        if (evidenceIds.Count > 0)
        {
            var cited = await db.Signals
                .Where(s => evidenceIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Summary, s.RawItem!.Url, s.RawItem.Source })
                .ToListAsync(cancellationToken);

            builder.Append("<b>Evidence</b>\n");
            foreach (var signal in cited)
            {
                builder.Append('S').Append(signal.Id).Append(' ')
                    .Append(System.Net.WebUtility.HtmlEncode(
                        signal.Summary.Length > 90 ? signal.Summary[..89] + "…" : signal.Summary));
                if (signal.Url is { Length: > 0 })
                {
                    builder.Append(" <a href=\"").Append(signal.Url).Append("\">[").Append(signal.Source).Append("]</a>");
                }

                builder.Append('\n');
            }
        }

        var text = builder.ToString().TrimEnd();
        return text.Length <= 3900 ? text : text[..3900] + "…";
    }

    private static void AppendLine(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append("<b>").Append(label).Append(":</b> ")
                .Append(System.Net.WebUtility.HtmlEncode(value)).Append('\n');
        }
    }

    private static double ComputeRating(IdeaEngine.Infrastructure.Persistence.Entities.IdeaEntity idea)
    {
        var scores = SafeDeserialize<Dictionary<string, double>>(idea.ScoresJson);
        var skeptic = SafeDeserialize<SkepticReview>(idea.SkepticJson);
        return IdeaScoring.Rating(scores, skeptic?.Confidence ?? 0);
    }

    private static List<long> ParseEvidence(string? evidenceJson) =>
        SafeDeserialize<List<long>>(evidenceJson) ?? [];

    /// <summary>For OUR OWN serialized jsonb columns (not LLM output - that's LlmJson's job).</summary>
    private static T? SafeDeserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json, LlmJson.Options);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private string BuildConfig()
    {
        var config = ingestionOptions.Value;
        return
            $"""
            <b>Config</b>
            Interval: every {config.IntervalHours:F1}h
            Max items/source: {config.MaxItemsPerSource}
            Run on startup: {config.RunOnStartup}
            Notify every cycle: {config.NotifyEveryCycle}
            Verbose item logging: {config.VerboseItemLogging}
            Sources: HackerNews, FourChan, Bluesky, Lemmy, RedditRss
            (tuning lives in appsettings.json → IdeaEngine:Sources)
            """;
    }

    private static string BuildHelp() =>
        """
        /status — pipeline state, counts by source/stage, recent runs
        /signals — latest extracted product-opportunity signals
        /best [n] — top signals by value (7 days), with idea links
        /idea 5 — full trace: thesis, skeptic verdict, evidence
        /top — best items of the last 24h
        /costs — AI spend, last 7 days
        /collect — run a full cycle now
        /collect hn|4chan|bluesky|lemmy|reddit — one source only
        /analyze — run AI triage on queued items now
        /ideate [n] — n builder-vs-skeptic idea sessions (default 1, max 10)
        /ideas — recent ideas, live and killed
        /advise — AI proposes pipeline/source improvements
        /config — current configuration
        """;

    private async Task ReplyAsync(string html, CancellationToken cancellationToken) =>
        await _bot!.SendMessage(
            chatId: _adminChatId,
            text: html,
            parseMode: ParseMode.Html,
            linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
            cancellationToken: cancellationToken);

    private static string FormatWait(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}h {value.Minutes:D2}m"
            : $"{value.Minutes}m {value.Seconds:D2}s";
    }
}
