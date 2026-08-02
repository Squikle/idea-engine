using System.Globalization;
using System.Text;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ideation;
using IdeaEngine.Infrastructure.Jobs;
using IdeaEngine.Infrastructure.Ingestion;
using IdeaEngine.Infrastructure.Notifications;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Research;
using IdeaEngine.Infrastructure.Triage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

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
    IStatusTracker statusTracker,
    INotifier notifier,
    IProgressNotifier progressNotifier,
    TimeProvider timeProvider,
    TimeZoneInfo timeZone,
    IOptions<IngestionOptions> ingestionOptions,
    IOptions<IdeaEngine.Infrastructure.Ai.AiBudgetOptions> budgetOptions,
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
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery],
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
                new BotCommand { Command = "drop", Description = "submit YOUR idea: shape, skeptic, web research" },
                new BotCommand { Command = "research", Description = "web-research a candidate idea" },
                new BotCommand { Command = "queue", Description = "jobs: running, waiting, failed" },
                new BotCommand { Command = "kill", Description = "your verdict: dismiss an idea" },
                new BotCommand { Command = "promote", Description = "your verdict: mark an idea hot" },
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
        if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(callback, cancellationToken);
            return;
        }

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
                "drop" => await StartDropAsync(argument, cancellationToken),
                "research" => await StartResearchAsync(argument, cancellationToken),
                "kill" => await SetIdeaStatusAsync(argument, "dismissed", cancellationToken),
                "promote" => await SetIdeaStatusAsync(argument, "hot", cancellationToken),
                "advise" => StartAdvise(),
                "ideas" => await SendIdeasPageAsync(argument, cancellationToken),
                "queue" => await SendQueueAsync(cancellationToken),
                "config" => BuildConfig(),
                "help" or "start" => BuildHelp(),
                _ => $"Unknown command: /{command} — try /help",
            };

            if (reply.Length > 0)
            {
                await ReplyAsync(reply, cancellationToken);
            }
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

        var byStatus = await db.RawItems
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        var signalsTotal = await db.Signals.CountAsync(cancellationToken);
        var signals24h = await db.Signals.CountAsync(
            s => s.CreatedAt >= now.AddHours(-24), cancellationToken);

        var ideaCounts = await db.Ideas
            .Where(i => i.Category != "meta")
            .GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        var unresearched = await db.Ideas
            .Where(i => i.Category != "meta" && i.Status == "candidate"
                && !db.ResearchReports.Any(r => r.IdeaId == i.Id))
            .CountAsync(cancellationToken);

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var spentToday = await db.AiLedger
            .Where(e => e.Day == today)
            .SumAsync(e => e.CostUsd, cancellationToken);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var spentMonth = await db.AiLedger
            .Where(e => e.Day >= monthStart)
            .SumAsync(e => e.CostUsd, cancellationToken);

        var lastRuns = await db.PipelineRuns
            .OrderByDescending(r => r.Id)
            .Take(4)
            .ToListAsync(cancellationToken);

        var builder = new StringBuilder();
        builder.Append(IdeaEngine.Infrastructure.Notifications.TrackBoardRenderer.Render(
            statusTracker.Snapshot(), now, timeZone));

        builder.Append("\n\n<b>🚰 Funnel</b> — waiting → done\n");
        builder.Append("items: ").Append(Get(byStatus, IdeaEngine.Core.Pipeline.ItemStatus.New)).Append(" new → ")
            .Append(Get(byStatus, IdeaEngine.Core.Pipeline.ItemStatus.PendingTriage)).Append(" queued → ")
            .Append(Get(byStatus, IdeaEngine.Core.Pipeline.ItemStatus.Triaged)).Append(" analyzed")
            .Append(" <i>(").Append(Get(byStatus, IdeaEngine.Core.Pipeline.ItemStatus.FilteredOut)).Append(" junk, ")
            .Append(Get(byStatus, IdeaEngine.Core.Pipeline.ItemStatus.Failed)).Append(" failed)</i>\n");
        builder.Append("signals: ").Append(signalsTotal).Append(" total · +")
            .Append(signals24h).Append(" in 24h\n");
        builder.Append("ideas: 🌱 ").Append(ideaCounts.GetValueOrDefault("candidate"))
            .Append(" (").Append(unresearched).Append(" await research) · 🤔 ")
            .Append(ideaCounts.GetValueOrDefault("uncertain") + ideaCounts.GetValueOrDefault("validated")).Append(" · 🔥 ")
            .Append(ideaCounts.GetValueOrDefault("hot")).Append(" · ☠️ ")
            .Append(ideaCounts.GetValueOrDefault("dismissed")).Append('\n');

        builder.Append("\n<b>🕐 Recent runs</b>\n");
        foreach (var run in lastRuns)
        {
            var took = run.FinishedAt is { } finished ? (finished - run.StartedAt).TotalSeconds : 0;
            builder.Append(run.Stage).Append(": ").Append(run.ItemsOut).Append('/').Append(run.ItemsIn)
                .Append(run.Errors > 0 ? $" ({run.Errors} err)" : string.Empty)
                .Append(" · ").Append(took.ToString("F0", CultureInfo.InvariantCulture)).Append("s\n");
        }

        var budget = budgetOptions.Value;
        builder.Append('\n').Append(IdeaEngine.Core.Common.Ui.Spend).Append(" today $")
            .Append(spentToday.ToString("F3", CultureInfo.InvariantCulture))
            .Append(" / ").Append(budget.GlobalDailyUsdCap.ToString("F0", CultureInfo.InvariantCulture))
            .Append(" · month $").Append(spentMonth.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" / ").Append(budget.GlobalMonthlyUsdCap.ToString("F0", CultureInfo.InvariantCulture));

        return builder.ToString();
    }

    private static int Get(
        IReadOnlyDictionary<IdeaEngine.Core.Pipeline.ItemStatus, int> counts,
        IdeaEngine.Core.Pipeline.ItemStatus status) => counts.GetValueOrDefault(status);

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

        var builder = new StringBuilder("<b>🔝 Top of the last 24h</b>\n");
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

        var builder = new StringBuilder("<b>🎯 Latest signals</b>\n");
        foreach (var signal in signals)
        {
            builder.Append("• ").Append(IdeaEngine.Core.Common.Ui.Kind(signal.Kind)).Append(' ')
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

        var builder = new StringBuilder("<b>💸 AI costs · 7 days</b>\n");
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

        return only is { } k ? $"📥 Collecting {k} now…" : "📥 Collecting all sources now…";
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

        return "🧠 Analyzing the queue now…";
    }

    private string StartIdeate(string? argument)
    {
        var count = 1;
        if (argument is not null && (!int.TryParse(argument, out count) || count < 1))
        {
            return "Usage: /ideate [1-10]";
        }

        if (!OperationGates.Ideation.Wait(0))
        {
            return "Ideation is already running — results will arrive when it finishes.";
        }

        var sessions = Math.Clamp(count, 1, 10);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var progress = await progressNotifier.StartAsync(
                        $"💡 Ideation · starting {sessions} session(s)…", CancellationToken.None);

                    using var scope = scopeFactory.CreateScope();
                    var ideation = scope.ServiceProvider.GetRequiredService<IdeationService>();
                    var result = await ideation.RunProductSessionsAsync(sessions, progress, CancellationToken.None);

                    await progress.CompleteAsync(
                        $"Ideation done · {result.Advanced} live · {result.Killed} killed · " +
                        $"${result.CostUsd.ToString("F2", CultureInfo.InvariantCulture)}",
                        CancellationToken.None);

                    await notifier.SendAsync(
                        IdeationFormatting.BuildResultsHtml(result), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Ideation batch failed");
                    await notifier.SendAsync("Ideation crashed — check logs.", CancellationToken.None);
                }
                finally
                {
                    OperationGates.Ideation.Release();
                }
            },
            CancellationToken.None);

        return string.Empty; // the progress message is the reply
    }

    private async Task<string> StartDropAsync(string? argument, CancellationToken cancellationToken)
    {
        if (argument is null || argument.Trim().Length < 12)
        {
            return "Usage: /drop followed by your idea pitch — a sentence or a paragraph, more context is better.";
        }

        var ack = await _bot!.SendMessage(
            chatId: _adminChatId, text: "📦 queuing…", cancellationToken: cancellationToken);

        using var scope = scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
        var (jobId, position) = await jobs.EnqueueAsync(
            "drop", new DropJobPayload(argument.Trim(), null), ack.MessageId, cancellationToken);

        await _bot!.EditMessageText(
            chatId: _adminChatId,
            messageId: ack.MessageId,
            text: $"📦 <b>Job #{jobId}</b> queued · position {position} · survives restarts\n" +
                "<i>The live progress log will reply to THIS message — tap it there. /queue for overview.</i>",
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);
        return string.Empty;
    }

    private async Task<string> StartResearchAsync(string? argument, CancellationToken cancellationToken)
    {
        if (!long.TryParse(argument, out var ideaId))
        {
            return "Usage: /research 5 (ids are shown by /ideas)";
        }

        var ack = await _bot!.SendMessage(
            chatId: _adminChatId, text: "🔎 queuing…", cancellationToken: cancellationToken);

        using var scope = scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
        var (jobId, position) = await jobs.EnqueueAsync(
            "research", new ResearchJobPayload(ideaId), ack.MessageId, cancellationToken);

        await _bot!.EditMessageText(
            chatId: _adminChatId,
            messageId: ack.MessageId,
            text: $"🔎 <b>Job #{jobId}</b> queued · position {position} · idea #{ideaId} (/idea {ideaId})\n" +
                "<i>The live progress log will reply to THIS message. /queue for overview.</i>",
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);
        return string.Empty;
    }

    private string StartAdvise()
    {
        if (!OperationGates.Ideation.Wait(0))
        {
            return "Ideation is already running — try again when it finishes.";
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var progress = await progressNotifier.StartAsync(
                        "🧭 Advisor · starting pipeline self-review…", CancellationToken.None);

                    using var scope = scopeFactory.CreateScope();
                    var ideation = scope.ServiceProvider.GetRequiredService<IdeationService>();
                    var result = await ideation.RunMetaSessionAsync(progress, CancellationToken.None);
                    if (result.StoppedReason is { } reason)
                    {
                        await progress.CompleteAsync(
                            $"Advisor stopped · {System.Net.WebUtility.HtmlEncode(reason)}", CancellationToken.None);
                        return;
                    }

                    await progress.CompleteAsync(
                        $"Advisor done · {result.ProposalsCount} proposals · journaled to journal/advice.md",
                        CancellationToken.None);
                    await notifier.SendAsync(result.Html, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Meta advice session failed");
                    await notifier.SendAsync("Advise crashed — check logs.", CancellationToken.None);
                }
                finally
                {
                    OperationGates.Ideation.Release();
                }
            },
            CancellationToken.None);

        return string.Empty; // the progress message is the reply
    }

    private static readonly string[] IdeaFilters = ["all", "top", "hot", "uncertain", "new", "dead"];

    private async Task<string> SendIdeasPageAsync(string? argument, CancellationToken cancellationToken)
    {
        var filter = argument?.Trim().ToLowerInvariant() switch
        {
            "top" => "top",
            "hot" => "hot",
            "uncertain" => "uncertain",
            "new" => "new",
            "dead" or "killed" => "dead",
            _ => "all",
        };

        var (text, keyboard) = await BuildIdeasPageAsync(filter, 1, cancellationToken);
        await _bot!.SendMessage(
            chatId: _adminChatId,
            text: text,
            parseMode: ParseMode.Html,
            linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
        return string.Empty; // sent with keyboard directly
    }

    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken cancellationToken)
    {
        try
        {
            if (callback.Message is not { } message || message.Chat.Id != _adminChatId)
            {
                return;
            }

            var parts = (callback.Data ?? string.Empty).Split('|');
            if (parts.Length >= 3 && parts[0] == "job" && parts[1] == "retry"
                && long.TryParse(parts[2], out var retryJobId))
            {
                using var scope = scopeFactory.CreateScope();
                var retried = await scope.ServiceProvider.GetRequiredService<JobService>()
                    .RetryAsync(retryJobId, cancellationToken);
                await _bot!.AnswerCallbackQuery(
                    callback.Id,
                    retried ? $"Job #{retryJobId} re-queued" : "Job not retryable",
                    cancellationToken: cancellationToken);
                return;
            }

            if (parts.Length >= 3 && parts[0] == "budget" && parts[1] == "bump")
            {
                using var scope = scopeFactory.CreateScope();
                var total = await scope.ServiceProvider
                    .GetRequiredService<IdeaEngine.Infrastructure.Ai.BudgetGuard>()
                    .BumpTodayAsync(5m, cancellationToken);
                await _bot!.AnswerCallbackQuery(
                    callback.Id, $"Caps +$5 for today (total bump ${total:F0})",
                    showAlert: true, cancellationToken: cancellationToken);
                if (long.TryParse(parts[2], out var bumpJobId))
                {
                    using var retryScope = scopeFactory.CreateScope();
                    await retryScope.ServiceProvider.GetRequiredService<JobService>()
                        .RetryAsync(bumpJobId, cancellationToken);
                    await notifier.SendAsync(
                        $"💸 Caps bumped +$5 for today · job #{bumpJobId} re-queued.", cancellationToken);
                }

                return;
            }

            if (parts.Length >= 3 && parts[0] == "ideas")
            {
                var page = int.TryParse(parts[2], out var parsed) ? parsed : 1;
                var (text, keyboard) = await BuildIdeasPageAsync(parts[1], page, cancellationToken);
                await _bot!.EditMessageText(
                    chatId: _adminChatId,
                    messageId: message.MessageId,
                    text: text,
                    parseMode: ParseMode.Html,
                    linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
            }

            await _bot!.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            await _bot!.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Callback handling failed");
        }
    }

    private async Task<(string Text, InlineKeyboardMarkup Keyboard)> BuildIdeasPageAsync(
        string filter, int page, CancellationToken cancellationToken)
    {
        const int pageSize = 8;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();

        var ideas = await db.Ideas
            .Where(i => i.Category != "meta")
            .OrderByDescending(i => i.Id)
            .Take(300)
            .ToListAsync(cancellationToken);

        var ideaIds = ideas.Select(i => i.Id).ToList();
        var latestReports = (await db.ResearchReports
                .Where(r => ideaIds.Contains(r.IdeaId))
                .OrderByDescending(r => r.Id)
                .Select(r => new { r.IdeaId, r.ReportJson })
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.IdeaId)
            .ToDictionary(
                g => g.Key,
                g => IdeaJson.SafeDeserialize<ResearchReportDto>(g.First().ReportJson));

        var scored = ideas
            .Select(i => new
            {
                Idea = i,
                Score = IdeaJson.ComputeScore(i, latestReports.GetValueOrDefault(i.Id)),
            })
            .ToList();

        var filtered = filter switch
        {
            "top" => scored.OrderByDescending(x => x.Score.Total).ThenByDescending(x => x.Idea.Id).ToList(),
            "hot" => scored.Where(x => x.Idea.Status == "hot")
                .OrderByDescending(x => x.Score.Total).ToList(),
            "uncertain" => scored.Where(x => x.Idea.Status is "uncertain" or "validated")
                .OrderByDescending(x => x.Score.Total).ToList(),
            "new" => scored.Where(x => x.Idea.Status == "candidate")
                .OrderByDescending(x => x.Score.Total).ToList(),
            "dead" => scored.Where(x => x.Idea.Status == "dismissed")
                .OrderByDescending(x => x.Idea.Id).ToList(),
            _ => scored.OrderByDescending(x => x.Idea.Id).ToList(),
        };

        var pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, pages);
        var slice = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var activeJobs = await db.Jobs.CountAsync(
            j => j.Status == "queued" || j.Status == "running", cancellationToken);

        var builder = new StringBuilder();
        builder.Append("<b>💡 Ideas · ").Append(FilterLabel(filter)).Append("</b> — ")
            .Append(filtered.Count).Append(" total <i>(⭐ researched · ≈ estimate)</i>\n");
        if (activeJobs > 0)
        {
            builder.Append("⏳ ").Append(activeJobs).Append(" job(s) in flight — /queue\n");
        }

        builder.Append('\n');

        if (slice.Count == 0)
        {
            builder.Append("<i>nothing here yet</i>\n");
        }

        foreach (var entry in slice)
        {
            var id = ('#' + entry.Idea.Id.ToString(CultureInfo.InvariantCulture)).PadRight(4);
            var pct = ((entry.Score.Total * 100).ToString("F0", CultureInfo.InvariantCulture) + "%")
                .PadLeft(4) + (entry.Score.Source == "research" ? "⭐" : "≈");
            var title = entry.Idea.Title.Length > 56 ? entry.Idea.Title[..55] + "…" : entry.Idea.Title;

            builder.Append(IdeaEngine.Core.Common.Ui.IdeaStatus(entry.Idea.Status))
                .Append(" <code>").Append(id).Append(pct).Append("</code> ")
                .Append(System.Net.WebUtility.HtmlEncode(title));
            if (entry.Idea.Origin == "operator")
            {
                builder.Append(" — <i>yours</i>");
            }

            builder.Append('\n');
        }

        builder.Append("\n/idea 5 for a trace card");

        var filterRow = IdeaFilters
            .Select(f => InlineKeyboardButton.WithCallbackData(
                (f == filter ? "• " : string.Empty) + FilterButton(f), $"ideas|{f}|1"))
            .ToArray();

        var navRow = new List<InlineKeyboardButton>();
        if (page > 1)
        {
            navRow.Add(InlineKeyboardButton.WithCallbackData("⬅️", $"ideas|{filter}|{page - 1}"));
        }

        navRow.Add(InlineKeyboardButton.WithCallbackData($"{page}/{pages}", $"ideas|{filter}|{page}"));
        if (page < pages)
        {
            navRow.Add(InlineKeyboardButton.WithCallbackData("➡️", $"ideas|{filter}|{page + 1}"));
        }

        var keyboard = new InlineKeyboardMarkup(
        [
            filterRow[..2],
            filterRow[2..],
            [.. navRow],
        ]);

        return (builder.ToString(), keyboard);
    }

    private static string FilterLabel(string filter) => filter switch
    {
        "top" => "best first",
        "hot" => "🔥 hot",
        "uncertain" => "🤔 uncertain",
        "new" => "🌱 new",
        "dead" => "☠️ killed",
        _ => "all by number",
    };

    private static string FilterButton(string filter) => filter switch
    {
        "top" => "Top",
        "hot" => "🔥",
        "uncertain" => "🤔",
        "new" => "🌱",
        "dead" => "☠️",
        _ => "All",
    };

    private async Task<string> SendQueueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var now = timeProvider.GetUtcNow();

        var active = await db.Jobs
            .Where(j => j.Status == "queued" || j.Status == "running")
            .OrderBy(j => j.Id)
            .ToListAsync(cancellationToken);
        var failed = await db.Jobs
            .Where(j => j.Status == "failed")
            .OrderByDescending(j => j.Id)
            .Take(3)
            .ToListAsync(cancellationToken);
        var doneToday = await db.Jobs.CountAsync(
            j => j.Status == "done" && j.UpdatedAt >= now.AddHours(-24), cancellationToken);

        var builder = new StringBuilder("<b>📋 Queue</b>\n");
        if (active.Count == 0)
        {
            builder.Append("😴 empty — nothing running or waiting\n");
        }

        var position = 0;
        foreach (var job in active)
        {
            position++;
            var marker = job.Status == "running" ? "▶️" : $"{position}.";
            builder.Append(marker).Append(" <b>#").Append(job.Id).Append("</b> ")
                .Append(job.Kind).Append(' ').Append(JobLabel(job))
                .Append(job.Status == "running"
                    ? $" · running {(now - job.UpdatedAt).TotalMinutes:F0}m"
                    : string.Empty)
                .Append('\n');
        }

        List<Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton> retryRow = [];
        if (failed.Count > 0)
        {
            builder.Append("\n<b>⛔ Failed</b>\n");
            foreach (var job in failed)
            {
                builder.Append("• <b>#").Append(job.Id).Append("</b> ").Append(job.Kind)
                    .Append(' ').Append(JobLabel(job)).Append(" — ")
                    .Append(System.Net.WebUtility.HtmlEncode(
                        IdeaEngine.Core.Common.TextClip.Clip(job.LastError ?? "?", 70)))
                    .Append('\n');
                retryRow.Add(Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton
                    .WithCallbackData($"🔁 #{job.Id}", $"job|retry|{job.Id}"));
            }
        }

        builder.Append("\n✅ ").Append(doneToday).Append(" done in 24h");

        await _bot!.SendMessage(
            chatId: _adminChatId,
            text: builder.ToString(),
            parseMode: ParseMode.Html,
            replyMarkup: retryRow.Count > 0
                ? new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(retryRow)
                : null,
            linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
            cancellationToken: cancellationToken);
        return string.Empty;
    }

    private static string JobLabel(IdeaEngine.Infrastructure.Persistence.Entities.JobEntity job)
    {
        try
        {
            if (job.Kind == "research")
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<ResearchJobPayload>(
                    job.PayloadJson, LlmJson.Options);
                return payload is null ? string.Empty : $"idea #{payload.IdeaId}";
            }

            if (job.Kind == "drop")
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<DropJobPayload>(
                    job.PayloadJson, LlmJson.Options);
                return payload is null
                    ? string.Empty
                    : payload.IdeaId is { } id
                        ? $"idea #{id}"
                        : $"\u201c{IdeaEngine.Core.Common.TextClip.Clip(payload.Pitch, 40)}\u201d";
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // fall through
        }

        return string.Empty;
    }

    private async Task<string> SetIdeaStatusAsync(
        string? argument, string newStatus, CancellationToken cancellationToken)
    {
        if (!long.TryParse(argument, out var ideaId))
        {
            return $"Usage: /{(newStatus == "hot" ? "promote" : "kill")} 5";
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var idea = await db.Ideas.FindAsync([ideaId], cancellationToken);
        if (idea is null)
        {
            return $"No idea #{ideaId}.";
        }

        var previous = idea.Status;
        idea.Status = newStatus;
        await db.SaveChangesAsync(cancellationToken);

        return $"{IdeaEngine.Core.Common.Ui.IdeaStatus(newStatus)} #{ideaId} " +
            $"{System.Net.WebUtility.HtmlEncode(idea.Title.Length > 60 ? idea.Title[..59] + "…" : idea.Title)}" +
            $" — {previous} → <b>{newStatus}</b> (your call, recorded)";
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
            foreach (var signalId in IdeaJson.ParseEvidence(idea.EvidenceJson))
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

        // Glance lines: cheapest model, cached per signal - repeat /best calls are free.
        var glanceService = scope.ServiceProvider.GetRequiredService<GlanceService>();
        var glances = await glanceService.EnsureGlancesAsync(
            [.. top.Select(s => new GlanceInput(s.Id, s.Summary, s.Kind))], cancellationToken);

        var builder = new StringBuilder("<b>🏆 Best signals · 7 days</b>\n");
        var rank = 1;
        foreach (var signal in top)
        {
            var line = glances.TryGetValue(signal.Id, out var glance)
                ? glance
                : signal.Summary.Length > 100 ? signal.Summary[..99] + "…" : signal.Summary;

            builder.Append(rank++).Append(". v")
                .Append(signal.Value.ToString("F2", CultureInfo.InvariantCulture))
                .Append(' ').Append(IdeaEngine.Core.Common.Ui.Kind(signal.Kind)).Append(' ')
                .Append(System.Net.WebUtility.HtmlEncode(line));

            if (signal.Url is { Length: > 0 })
            {
                builder.Append(" <a href=\"").Append(signal.Url).Append("\">[").Append(signal.Source).Append("]</a>");
            }

            if (signalToIdea.TryGetValue(signal.Id, out var idea))
            {
                builder.Append(" → ").Append(IdeaEngine.Core.Common.Ui.IdeaStatus(idea.Status)).Append('#').Append(idea.IdeaId);
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
        var lastResearch = await db.ResearchReports
            .Where(r => r.IdeaId == ideaId)
            .OrderByDescending(r => r.Id)
            .Select(r => new { r.Verdict, r.Confidence, r.SearchesUsed, r.CostUsd, r.CreatedAt, r.ReportJson })
            .FirstOrDefaultAsync(cancellationToken);
        var researchReport = lastResearch is null
            ? null
            : IdeaJson.SafeDeserialize<IdeaEngine.Infrastructure.Research.ResearchReportDto>(lastResearch.ReportJson);

        var builder = new StringBuilder();
        builder.Append(IdeaEngine.Core.Common.Ui.IdeaStatus(idea.Status)).Append(" <b>#").Append(idea.Id).Append(" · ")
            .Append(System.Net.WebUtility.HtmlEncode(idea.Title)).Append("</b>\n")
            .Append(idea.Status).Append(" · ")
            .Append(idea.Category).Append(" · effort ").Append(idea.EffortScale).Append('\n');

        var score = IdeaJson.ComputeScore(idea, researchReport);
        if (score.Source != "none")
        {
            builder.Append("\n⭐ <b>Score ")
                .Append((score.Total * 100).ToString("F0", CultureInfo.InvariantCulture))
                .Append("%</b> — ")
                .Append(score.Source == "research" ? "from web research" : "skeptic estimate only")
                .Append(" · confidence ")
                .Append((score.Confidence * 100).ToString("F0", CultureInfo.InvariantCulture)).Append("%\n");

            // Aligned 2x2 grid: monospace so percentages line up at a glance.
            string Cell(string icon, string label, string key) =>
                score.Categories.TryGetValue(key, out var v)
                    ? $"{icon} {label.PadRight(6)}{((v * 100).ToString("F0", CultureInfo.InvariantCulture) + "%").PadLeft(4)}"
                    : $"{icon} {label.PadRight(6)}  —";
            builder.Append("<code>").Append(Cell("💰", "demand", "demand")).Append("   ")
                .Append(Cell("💵", "pay", "pay")).Append("</code>\n");
            builder.Append("<code>").Append(Cell("🔨", "build", "build")).Append("   ")
                .Append(Cell("🏪", "gap", "gap")).Append("</code>\n");
        }

        builder.Append('\n').Append(System.Net.WebUtility.HtmlEncode(idea.Thesis)).Append('\n');

        AppendLine(builder, "Target", idea.TargetUser);
        AppendLine(builder, "Monetization", idea.Monetization);
        AppendLine(builder, "Distribution", idea.DistributionNote);

        // ---- The journey: chronological stages, last one is the verdict that counts. ----
        builder.Append("\n<b>🛤 Journey</b>\n");

        if (skeptic is not null)
        {
            var advanced = string.Equals(skeptic.Verdict, "advance", StringComparison.OrdinalIgnoreCase);
            builder.Append("1️⃣ 🥊 <b>Skeptic gate</b> <i>(AI opinion, no web evidence yet)</i>\n")
                .Append(advanced ? "🟢 voted advance" : "☠️ voted kill")
                .Append(" · ").Append((skeptic.Confidence * 100).ToString("F0", CultureInfo.InvariantCulture))
                .Append("% sure\n");
            foreach (var reason in (skeptic.KillReasons ?? []).Concat(skeptic.Weaknesses ?? []).Take(2))
            {
                builder.Append("– ").Append(System.Net.WebUtility.HtmlEncode(
                    reason.Length > 110 ? reason[..109] + "…" : reason)).Append('\n');
            }
        }

        if (lastResearch is not null)
        {
            builder.Append("2️⃣ 🔎 <b>Web research — FINAL verdict</b> <i>(evidence-based, overrides the gate)</i>\n")
                .Append(IdeaEngine.Core.Common.Ui.Verdict(lastResearch.Verdict))
                .Append(" · ").Append((lastResearch.Confidence * 100).ToString("F0", CultureInfo.InvariantCulture))
                .Append("% confident · ").Append(lastResearch.SearchesUsed).Append(" searches · $")
                .Append(lastResearch.CostUsd.ToString("F3", CultureInfo.InvariantCulture)).Append('\n');

            var answers = researchReport?.Answers ?? [];
            var answeredCount = answers.Count(a => a.IsAnswered);
            var open = answers.Where(a => !a.IsAnswered && a.Question is { Length: > 0 }).Take(3).ToList();
            if (answers.Count > 0)
            {
                builder.Append("❓ ").Append(answeredCount).Append('/').Append(answers.Count)
                    .Append(" questions answered\n");
            }

            if (open.Count > 0)
            {
                builder.Append("<b>🕳 Still open</b>\n");
                foreach (var item in open)
                {
                    builder.Append("• ").Append(System.Net.WebUtility.HtmlEncode(
                        item.Question!.Length > 100 ? item.Question[..99] + "…" : item.Question)).Append('\n');
                }
            }

            var competitors = (researchReport?.Competitors ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Take(6)
                .ToList();
            if (competitors.Count > 0)
            {
                builder.Append("<b>🏪 Competitors</b>\n");
                foreach (var competitor in competitors)
                {
                    builder.Append("• ");
                    if (competitor.Url is { Length: > 0 })
                    {
                        builder.Append("<a href=\"").Append(competitor.Url).Append("\">")
                            .Append(System.Net.WebUtility.HtmlEncode(competitor.Name!)).Append("</a>");
                    }
                    else
                    {
                        builder.Append(System.Net.WebUtility.HtmlEncode(competitor.Name!));
                    }

                    if (competitor.Why is { Length: > 0 } why)
                    {
                        builder.Append(" — ").Append(System.Net.WebUtility.HtmlEncode(
                            why.Length > 90 ? why[..89] + "…" : why));
                    }

                    builder.Append('\n');
                }
            }

            var skepticKilled = skeptic is not null
                && !string.Equals(skeptic.Verdict, "advance", StringComparison.OrdinalIgnoreCase);
            if (skepticKilled && lastResearch.Verdict != "no-go")
            {
                builder.Append("⚔️ <i>Stages disagree → status 🤔 uncertain. Your call: /kill ")
                    .Append(idea.Id).Append(" · /promote ").Append(idea.Id)
                    .Append(" · rerun /research ").Append(idea.Id).Append("</i>\n");
            }
        }
        else
        {
            builder.Append("2️⃣ 🔎 <b>Web research</b> — not run yet → /research ").Append(idea.Id).Append('\n');
            if (skeptic?.ResearchQuestions is { Count: > 0 } questions)
            {
                builder.Append("<b>❓ It would investigate</b>\n");
                foreach (var question in questions.Take(5))
                {
                    builder.Append("? ").Append(System.Net.WebUtility.HtmlEncode(question)).Append('\n');
                }
            }
        }

        var evidenceIds = IdeaJson.ParseEvidence(idea.EvidenceJson).Take(6).ToList();
        if (evidenceIds.Count > 0)
        {
            var cited = await db.Signals
                .Where(s => evidenceIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Summary, s.RawItem!.Url, s.RawItem.Source })
                .ToListAsync(cancellationToken);

            builder.Append("<b>🎯 Evidence</b>\n");
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
        <b>Flow:</b> collect → analyze → signals → ideas → research

        <b>Run</b>
        /collect — fetch all sources now (or: <code>/collect hn</code>, 4chan, bluesky, lemmy, reddit)
        /analyze — AI triage of queued items
        /ideate 3 — AI builder-vs-skeptic sessions from your signals
        /drop your pitch here — YOUR idea: shaped → skeptic → web research
        /research 5 — web-validate idea number 5
        /kill 5 · /promote 5 — override any verdict with YOUR decision
        /queue — jobs running/waiting/failed (retry buttons)
        /advise — AI reviews the pipeline itself

        <b>View</b>
        /best 8 — top signals, glance lines, idea links
        /ideas — browse with buttons (filters + pages) · /ideas top = best first
        /idea 5 — full trace card for idea number 5
        /signals · /top · /status · /costs · /config

        <b>Scores</b>
        ⭐ = graded by web research (evidence) · ≈ = skeptic estimate (no research yet)
        Score = ingredients (demand/pay/build/gap). Status = the decision — one fatal
        flaw kills an idea regardless of a pretty score.

        <i>Numbers like 5 are idea ids — /ideas shows them as #5.</i>
        """;

    private async Task ReplyAsync(string html, CancellationToken cancellationToken)
    {
        try
        {
            await _bot!.SendMessage(
                chatId: _adminChatId,
                text: html,
                parseMode: ParseMode.Html,
                linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                cancellationToken: cancellationToken);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
        {
            // A formatting slip must never kill a command: retry as plain text.
            logger.LogWarning(ex, "HTML reply rejected; resending as plain text");
            var plain = System.Net.WebUtility.HtmlDecode(
                System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", string.Empty));
            await _bot!.SendMessage(
                chatId: _adminChatId,
                text: plain,
                linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                cancellationToken: cancellationToken);
        }
    }

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
