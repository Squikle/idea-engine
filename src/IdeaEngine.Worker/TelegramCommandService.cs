using System.Globalization;
using System.Text;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Core.Sources;
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
    private ITelegramBotClient? _bot;
    private long _adminChatId;
    private DateTimeOffset _startedAt;

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
                new BotCommand { Command = "signals", Description = "latest extracted signals" },
                new BotCommand { Command = "top", Description = "top items from the last 24h" },
                new BotCommand { Command = "costs", Description = "AI spend, last 7 days" },
                new BotCommand { Command = "collect", Description = "run a cycle now (optionally one source)" },
                new BotCommand { Command = "analyze", Description = "run AI triage on the queue now" },
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
                "costs" => await BuildCostsAsync(cancellationToken),
                "collect" => StartCollect(argument),
                "analyze" => StartAnalyze(),
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
        /top — best items of the last 24h
        /costs — AI spend, last 7 days
        /collect — run a full cycle now
        /collect hn|4chan|bluesky|lemmy|reddit — one source only
        /analyze — run AI triage on queued items now
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
