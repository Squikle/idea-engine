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
                new BotCommand { Command = "ideate", Description = "AI idea sessions (optional playbook lens)" },
                new BotCommand { Command = "playbooks", Description = "strategic lenses the machine uses" },
                new BotCommand { Command = "drop", Description = "submit YOUR idea: shape, skeptic, web research" },
                new BotCommand { Command = "research", Description = "web-research a candidate idea" },
                new BotCommand { Command = "queue", Description = "jobs: running, held, failed + controls" },
                new BotCommand { Command = "cancel", Description = "cancel a queued/held job" },
                new BotCommand { Command = "dig", Description = "excavate a niche/topic into opportunities" },
                new BotCommand { Command = "audit", Description = "find ideas that fell through the cracks" },
                new BotCommand { Command = "sweep", Description = "re-eval verdicts made by an older brain" },
                new BotCommand { Command = "verify", Description = "mark idea as reviewed by you" },
                new BotCommand { Command = "note", Description = "argue: attach a note the next research must address" },
                new BotCommand { Command = "notes", Description = "your notes: all ideas or one" },
                new BotCommand { Command = "find", Description = "fuzzy-search ideas by words/context" },
                new BotCommand { Command = "appeal", Description = "stronger model reviews a verdict" },
                new BotCommand { Command = "partner", Description = "your right-hand's blunt take on an idea" },
                new BotCommand { Command = "origin", Description = "your original pitch + the idea at its best" },
                new BotCommand { Command = "models", Description = "see or swap the model behind each stage" },
                new BotCommand { Command = "mine", Description = "mine AI memory for pains (reply to dig deeper)" },
                new BotCommand { Command = "chat", Description = "talk to your right hand (or just type without /)" },
                new BotCommand { Command = "bump", Description = "+$5 to today's AI caps" },
                new BotCommand { Command = "kill", Description = "your verdict: dismiss an idea" },
                new BotCommand { Command = "promote", Description = "your verdict: mark an idea hot" },
                new BotCommand { Command = "ideas", Description = "recent ideas (live and killed)" },
                new BotCommand { Command = "signal", Description = "one signal's full lineage card" },
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

        // Replying to a MINE card continues that excavation; any other plain text is a
        // conversation with the right hand. Slash = commands, as always.
        if (!text.StartsWith('/'))
        {
            if (update.Message.ReplyToMessage?.Text is { } repliedText
                && repliedText.StartsWith("⛏ MINE", StringComparison.Ordinal))
            {
                StartMine(text.Trim(), continuation: true);
                return;
            }

            await HandleHandMessageAsync(text.Trim(), cancellationToken);
            return;
        }

        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant().TrimStart('/');
        var argument = parts.Length > 1 ? parts[1].Trim() : null;

        // Compact form /idea5 → command "idea", argument "5" (Telegram links only the
        // bare command token, so every id hint we emit is tappable this way).
        if (IdeaEngine.Core.Common.Ui.SplitCompactCommand(command) is { } compact)
        {
            command = compact.Command;
            argument = argument is { Length: > 0 } ? compact.Id + " " + argument : compact.Id;
        }

        try
        {
            var reply = command switch
            {
                "status" => await SendStatusAsync(cancellationToken),
                "top" => await BuildTopAsync(cancellationToken),
                "signals" => await SendSignalsPageAsync(argument, cancellationToken),
                "signal" => await BuildSignalDetailAsync(argument, cancellationToken),
                "best" => await BuildBestAsync(argument, cancellationToken),
                "idea" => await SendIdeaDetailAsync(argument, cancellationToken),
                "costs" => await BuildCostsAsync(cancellationToken),
                "collect" => StartCollect(argument),
                "analyze" => StartAnalyze(),
                "playbooks" => BuildPlaybooks(),
                "ideate" => StartIdeate(argument),
                "drop" => await StartDropAsync(argument, cancellationToken),
                "research" => await StartResearchAsync(argument, cancellationToken),
                "dig" => await StartDigAsync(argument, cancellationToken),
                "audit" => StartAudit(),
                "sweep" => StartSweep(),
                "kill" => await SetIdeaStatusAsync(argument, "dismissed", cancellationToken),
                "verify" => await VerifyIdeaAsync(argument, cancellationToken),
                "note" => await AddNoteAsync(argument, cancellationToken),
                "notes" => await ListNotesAsync(argument, cancellationToken),
                "find" => await FindIdeasAsync(argument, cancellationToken),
                "appeal" => StartAppeal(argument),
                "partner" => StartPartner(argument),
                "origin" => await BuildOriginAsync(argument, cancellationToken),
                "models" => await HandleModelsAsync(argument, cancellationToken),
                "mine" => HandleMineCommand(argument),
                "chat" => await HandleChatCommandAsync(argument, cancellationToken),
                "bump" => await BumpBudgetAsync(cancellationToken),
                "promote" => await SetIdeaStatusAsync(argument, "hot", cancellationToken),
                "advise" => StartAdvise(),
                "ideas" => await SendIdeasPageAsync(argument, cancellationToken),
                "queue" => await SendQueueAsync(cancellationToken),
                "cancel" => await CancelJobAsync(argument, cancellationToken),
                "config" => await HandleConfigAsync(argument, cancellationToken),
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

    private async Task<string> SendStatusAsync(CancellationToken cancellationToken)
    {
        var text = await BuildStatusAsync(cancellationToken);
        await _bot!.SendMessage(
            chatId: _adminChatId,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
            [
                [Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("💸 +$5 today", "budget|bump|0")],
            ]),
            linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
            cancellationToken: cancellationToken);
        return string.Empty;
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
        var bump = await scope.ServiceProvider
            .GetRequiredService<IdeaEngine.Infrastructure.Ai.BudgetGuard>()
            .GetTodayBumpAsync(cancellationToken);
        var effectiveDaily = budget.GlobalDailyUsdCap + bump;
        builder.Append('\n').Append(IdeaEngine.Core.Common.Ui.Spend).Append(" today <b>$")
            .Append(spentToday.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" / $").Append(effectiveDaily.ToString("F0", CultureInfo.InvariantCulture)).Append("</b>");
        if (bump > 0)
        {
            builder.Append(" <i>(base $").Append(budget.GlobalDailyUsdCap.ToString("F0", CultureInfo.InvariantCulture))
                .Append(" + $").Append(bump.ToString("F0", CultureInfo.InvariantCulture)).Append(" bumped)</i>");
        }

        builder.Append(" · month $").Append(spentMonth.ToString("F2", CultureInfo.InvariantCulture))
            .Append(" / $").Append(budget.GlobalMonthlyUsdCap.ToString("F0", CultureInfo.InvariantCulture))
            .Append("\n<i>stage caps rise with the bump too; monthly is the hard wall</i>");

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

    private static readonly string[] SignalFilters = ["all", "pain", "wish", "trend", "mine", "unused", "used"];

    private async Task<string> SendSignalsPageAsync(string? argument, CancellationToken cancellationToken)
    {
        var filter = argument?.Trim().ToLowerInvariant() switch
        {
            "pain" => "pain",
            "wish" => "wish",
            "trend" => "trend",
            "mine" => "mine",
            "unused" => "unused",
            "used" => "used",
            _ => "all",
        };
        var (text, keyboard) = await BuildSignalsPageAsync(filter, 1, "value", group: false, cancellationToken);
        await _bot!.SendMessage(
            chatId: _adminChatId,
            text: text,
            parseMode: ParseMode.Html,
            linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
        return string.Empty;
    }

    /// <summary>EvidenceJson reverse map: which idea consumed which signal.</summary>
    private static Dictionary<long, (long IdeaId, string Status)> BuildSignalConsumerMap(
        IEnumerable<(long Id, string Status, string? EvidenceJson)> ideas)
    {
        var map = new Dictionary<long, (long, string)>();
        foreach (var (ideaId, status, evidenceJson) in ideas)
        {
            foreach (var signalId in IdeaJson.ParseEvidence(evidenceJson))
            {
                map.TryAdd(signalId, (ideaId, status));
            }
        }

        return map;
    }

    private async Task<(string Text, InlineKeyboardMarkup Keyboard)> BuildSignalsPageAsync(
        string filter, int page, string sort, bool group, CancellationToken cancellationToken)
    {
        const int pageSize = 10;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();

        var rows = await db.Signals
            .OrderByDescending(s => s.Id)
            .Take(600)
            .Select(s => new
            {
                s.Id,
                s.Kind,
                s.Summary,
                s.Glance,
                s.CommercialSentiment,
                s.Confidence,
                s.Novelty,
                s.CreatedAt,
                Source = s.RawItem!.Source,
            })
            .ToListAsync(cancellationToken);

        var consumers = BuildSignalConsumerMap((await db.Ideas
                .OrderByDescending(i => i.Id)
                .Take(400)
                .Select(i => new { i.Id, i.Status, i.EvidenceJson })
                .ToListAsync(cancellationToken))
            .Select(i => (i.Id, i.Status, i.EvidenceJson)));

        var scored = rows
            .Select(s => new
            {
                s.Id,
                s.Kind,
                Text = s.Glance is { Length: > 0 } ? s.Glance : s.Summary,
                s.CreatedAt,
                s.Source,
                Value = IdeaEngine.Core.Pipeline.SignalScoring.Value(s.Confidence, s.Novelty, s.CommercialSentiment),
                Consumer = consumers.TryGetValue(s.Id, out var c) ? c : ((long, string)?)null,
            })
            .ToList();

        var selected = filter switch
        {
            "pain" => scored.Where(x => x.Kind is "pain" or "complaint"),
            "wish" => scored.Where(x => x.Kind is "wish" or "demand"),
            "trend" => scored.Where(x => x.Kind == "trend"),
            "mine" => scored.Where(x => x.Source == IdeaEngine.Core.Sources.SourceKind.AiMine),
            "unused" => scored.Where(x => x.Consumer is null),
            "used" => scored.Where(x => x.Consumer is not null),
            _ => scored.AsEnumerable(),
        };

        var filtered = sort == "new"
            ? selected.OrderByDescending(x => x.Id).ToList()
            : selected.OrderByDescending(x => x.Value).ThenByDescending(x => x.Id).ToList();

        var pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, pages);
        var slice = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var builder = new StringBuilder();
        builder.Append("<b>🎯 Signals · ").Append(filter).Append("</b> — ").Append(filtered.Count)
            .Append(" of last 600 · by ").Append(sort == "new" ? "newest" : "value").Append('\n');
        builder.Append("<i>tap s-ids for lineage · /ideate from N M builds from chosen ones</i>\n\n");

        DateOnly? lastDay = null;
        foreach (var entry in slice)
        {
            if (group)
            {
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.CreatedAt, timeZone).Date);
                if (day != lastDay)
                {
                    lastDay = day;
                    builder.Append("— <i>").Append(DayLabel(day)).Append("</i> —\n");
                }
            }

            builder.Append(IdeaEngine.Core.Common.Ui.Cmd("signal", entry.Id))
                .Append(" <code>").Append(((entry.Value * 100).ToString("F0", CultureInfo.InvariantCulture) + "%").PadLeft(4))
                .Append("</code> ").Append(IdeaEngine.Core.Common.Ui.Kind(entry.Kind)).Append(' ')
                .Append(System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(entry.Text, 80)));
            if (entry.Source == IdeaEngine.Core.Sources.SourceKind.AiMine)
            {
                builder.Append(" ⛏");
            }

            if (entry.Consumer is { } consumer)
            {
                builder.Append(" → ").Append(IdeaEngine.Core.Common.Ui.IdeaStatus(consumer.Item2))
                    .Append('#').Append(consumer.Item1);
            }

            builder.Append('\n');
        }

        if (slice.Count == 0)
        {
            builder.Append("<i>nothing here</i>\n");
        }

        var filterRow = SignalFilters
            .Select(f => InlineKeyboardButton.WithCallbackData(
                (f == filter ? "• " : string.Empty) + f, $"sigs|{f}|1|{sort}|{(group ? 1 : 0)}"))
            .ToArray();
        var navRow = new List<InlineKeyboardButton>();
        if (page > 1)
        {
            navRow.Add(InlineKeyboardButton.WithCallbackData("⬅️", $"sigs|{filter}|{page - 1}|{sort}|{(group ? 1 : 0)}"));
        }

        navRow.Add(InlineKeyboardButton.WithCallbackData($"{page}/{pages}", $"sigs|{filter}|{page}|{sort}|{(group ? 1 : 0)}"));
        if (page < pages)
        {
            navRow.Add(InlineKeyboardButton.WithCallbackData("➡️", $"sigs|{filter}|{page + 1}|{sort}|{(group ? 1 : 0)}"));
        }

        navRow.Add(sort == "new"
            ? InlineKeyboardButton.WithCallbackData("💎 by value", $"sigs|{filter}|1|value|{(group ? 1 : 0)}")
            : InlineKeyboardButton.WithCallbackData("🕐 newest", $"sigs|{filter}|1|new|{(group ? 1 : 0)}"));
        navRow.Add(InlineKeyboardButton.WithCallbackData(group ? "📅 off" : "📅 group", $"sigs|{filter}|1|{sort}|{(group ? 0 : 1)}"));

        return (builder.ToString().TrimEnd(), new InlineKeyboardMarkup(
        [
            filterRow[..4],
            filterRow[4..],
            [.. navRow],
        ]));
    }

    private string DayLabel(DateOnly day)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeZone).Date);
        return day == today ? "today"
            : day == today.AddDays(-1) ? "yesterday"
            : day.ToString(day.Year == today.Year ? "MMM d" : "MMM d yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>The lineage card: signal → its source process → its consumers.</summary>
    private async Task<string> BuildSignalDetailAsync(string? argument, CancellationToken cancellationToken)
    {
        if (!long.TryParse(argument?.Trim().Split(' ')[0], out var signalId))
        {
            return "Usage: /signal 123 — full lineage of one signal (/signals to browse)";
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var signal = await db.Signals
            .Where(s => s.Id == signalId)
            .Select(s => new
            {
                s.Id,
                s.Kind,
                s.Summary,
                s.Glance,
                s.Audience,
                s.CommercialSentiment,
                s.Confidence,
                s.Novelty,
                s.Model,
                s.CreatedAt,
                RawSource = s.RawItem!.Source,
                RawCommunity = s.RawItem.Community,
                RawUrl = s.RawItem.Url,
                RawTitle = s.RawItem.Title,
                RawScore = s.RawItem.Score,
                RawComments = s.RawItem.CommentCount,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (signal is null)
        {
            return $"No signal s{signalId}.";
        }

        var value = IdeaEngine.Core.Pipeline.SignalScoring.Value(signal.Confidence, signal.Novelty, signal.CommercialSentiment);
        var builder = new StringBuilder();
        builder.Append("🎯 <b>Signal s").Append(signal.Id).Append("</b> · ")
            .Append(IdeaEngine.Core.Common.Ui.Kind(signal.Kind)).Append(' ').Append(signal.Kind)
            .Append(" · 💎 ").Append((value * 100).ToString("F0", CultureInfo.InvariantCulture)).Append("%\n\n")
            .Append(System.Net.WebUtility.HtmlEncode(signal.Summary)).Append('\n');
        if (signal.Audience is { Length: > 0 })
        {
            builder.Append("👥 ").Append(System.Net.WebUtility.HtmlEncode(signal.Audience)).Append('\n');
        }

        builder.Append("<i>").Append(signal.CommercialSentiment.Replace('_', ' '))
            .Append(" · confidence ").Append((signal.Confidence * 100).ToString("F0", CultureInfo.InvariantCulture))
            .Append("% · novelty ").Append((signal.Novelty * 100).ToString("F0", CultureInfo.InvariantCulture))
            .Append("% · ").Append(signal.Model).Append("</i>\n");

        builder.Append("\n<b>⬅️ Born from</b>\n");
        if (signal.RawSource == IdeaEngine.Core.Sources.SourceKind.AiMine)
        {
            builder.Append("⛏ /mine angle <b>").Append(System.Net.WebUtility.HtmlEncode(signal.RawCommunity ?? "?"))
                .Append("</b>\n<i>").Append(System.Net.WebUtility.HtmlEncode(signal.RawTitle)).Append("</i>\n");
        }
        else
        {
            builder.Append(signal.RawSource.ToString()).Append(" · ")
                .Append(System.Net.WebUtility.HtmlEncode(signal.RawCommunity ?? "-"))
                .Append(" · score ").Append(signal.RawScore).Append(" · ").Append(signal.RawComments).Append(" comments\n")
                .Append(System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(signal.RawTitle, 120)));
            if (signal.RawUrl is { Length: > 0 })
            {
                builder.Append(" <a href=\"").Append(signal.RawUrl).Append("\">[open]</a>");
            }

            builder.Append('\n');
        }

        var consumerIdeas = (await db.Ideas
                .OrderByDescending(i => i.Id)
                .Take(400)
                .Select(i => new { i.Id, i.Title, i.Status, i.EvidenceJson })
                .ToListAsync(cancellationToken))
            .Where(i => IdeaJson.ParseEvidence(i.EvidenceJson).Contains(signal.Id))
            .ToList();
        builder.Append("\n<b>➡️ Consumed by</b>\n");
        if (consumerIdeas.Count == 0)
        {
            builder.Append("<i>no idea yet — /ideate from ").Append(signal.Id).Append(" uses it directly</i>\n");
        }
        else
        {
            foreach (var idea in consumerIdeas)
            {
                builder.Append(IdeaEngine.Core.Common.Ui.IdeaStatus(idea.Status)).Append(' ')
                    .Append(IdeaEngine.Core.Common.Ui.Cmd("idea", idea.Id)).Append(' ')
                    .Append(System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(idea.Title, 60)))
                    .Append('\n');
            }
        }

        builder.Append("\n<i>").Append(DayLabel(DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(signal.CreatedAt, timeZone).Date))).Append("</i>");
        return builder.ToString();
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

        var credits = await scope.ServiceProvider
            .GetRequiredService<IdeaEngine.Infrastructure.Ai.OpenRouterChatClient>()
            .GetCreditsAsync(cancellationToken);
        if (credits is { } wallet)
        {
            var remaining = wallet.Total - wallet.Used;
            builder.Append("\n\n🏦 <b>OpenRouter wallet:</b> $")
                .Append(remaining.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" left of $").Append(wallet.Total.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" deposited");
            if (remaining < 2)
            {
                builder.Append(" — <b>top up soon</b> (openrouter.ai → Credits)");
            }
        }

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

    private static string BuildPlaybooks()
    {
        var builder = new StringBuilder("<b>📚 Playbooks</b> — lenses the machine thinks through\n");
        builder.Append("<i>Rotation: every ideation session samples 1-2 automatically. ")
            .Append("Force one: /ideate 3 nostalgia</i>\n\n");
        foreach (var playbook in IdeaEngine.Core.Pipeline.Playbooks.All)
        {
            builder.Append(playbook.Emoji).Append(" <code>").Append(playbook.Key).Append("</code> — <b>")
                .Append(playbook.Title).Append("</b>\n")
                .Append(System.Net.WebUtility.HtmlEncode(playbook.Guidance)).Append("\n\n");
        }

        return builder.ToString().TrimEnd();
    }

    private string StartIdeate(string? argument)
    {
        var tokens = (argument ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Explicit-control mode: /ideate from 12 45 [playbook] - exactly those signals.
        if (tokens.Length > 1 && tokens[0].Equals("from", StringComparison.OrdinalIgnoreCase))
        {
            var signalIds = tokens[1..]
                .Where(t => long.TryParse(t, out _))
                .Select(long.Parse)
                .Distinct()
                .Take(8)
                .ToList();
            var fromPlaybook = tokens[1..].FirstOrDefault(t =>
                !long.TryParse(t, out _) && IdeaEngine.Core.Pipeline.Playbooks.TryGet(t, out _));
            if (signalIds.Count == 0)
            {
                return "Usage: /ideate from 12 45 [playbook] — builds ONE idea from exactly those signals (/signals to browse)";
            }

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
                            $"💡 Ideation · from your chosen signals: {string.Join(", ", signalIds.Select(i => $"s{i}"))}…",
                            CancellationToken.None);
                        using var scope = scopeFactory.CreateScope();
                        var ideation = scope.ServiceProvider.GetRequiredService<IdeationService>();
                        var result = await ideation.RunFromSignalsAsync(signalIds, fromPlaybook, progress, CancellationToken.None);
                        await progress.CompleteAsync(
                            result.StoppedReason is { } why
                                ? $"💡 ⛔ {why}"
                                : $"💡 done · {result.Advanced} live · {result.Killed} killed · ${result.CostUsd.ToString("F2", CultureInfo.InvariantCulture)}",
                            CancellationToken.None);
                        if (result.Lines.Count > 0)
                        {
                            await notifier.SendAsync(string.Join('\n', result.Lines), CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Ideate-from failed");
                        await notifier.SendAsync("💡 ideation from signals crashed — check logs.", CancellationToken.None);
                    }
                    finally
                    {
                        OperationGates.Ideation.Release();
                    }
                },
                CancellationToken.None);
            return string.Empty;
        }

        var count = 1;
        string? playbookKey = null;
        foreach (var token in tokens)
        {
            if (int.TryParse(token, out var parsedCount))
            {
                count = parsedCount;
            }
            else if (IdeaEngine.Core.Pipeline.Playbooks.TryGet(token, out var lens))
            {
                playbookKey = lens.Key;
            }
            else
            {
                return $"Unknown playbook '{token}' — /playbooks lists them. Usage: /ideate 3 nostalgia · /ideate from 12 45";
            }
        }

        if (count < 1)
        {
            return "Usage: /ideate [1-10] [playbook]";
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
                        playbookKey is null
                            ? $"💡 Ideation · {sessions} session(s), rotating lenses…"
                            : $"💡 Ideation · {sessions} session(s) through the {playbookKey} lens…",
                        CancellationToken.None);

                    using var scope = scopeFactory.CreateScope();
                    var ideation = scope.ServiceProvider.GetRequiredService<IdeationService>();
                    var result = await ideation.RunProductSessionsAsync(sessions, playbookKey, progress, CancellationToken.None);

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

    private async Task<string> StartDigAsync(string? argument, CancellationToken cancellationToken)
    {
        var topic = argument?.Trim();
        if (topic is null || topic.Length < 3)
        {
            return "Usage: /dig cycling — excavates a niche/topic/pain into a saturation map + spawned ideas";
        }

        var ack = await _bot!.SendMessage(
            chatId: _adminChatId, text: "⛏ queuing…", cancellationToken: cancellationToken);

        using var scope = scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
        var (jobId, position) = await jobs.EnqueueAsync(
            "dig", new DigJobPayload(topic), ack.MessageId, cancellationToken);

        await _bot!.EditMessageText(
            chatId: _adminChatId,
            messageId: ack.MessageId,
            text: $"⛏ <b>Job #{jobId}</b> queued · position {position} · “{System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(topic, 50))}”\n" +
                "<i>The live progress log will reply to THIS message. /queue for overview.</i>",
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);
        return string.Empty;
    }

    private string StartSweep()
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var progress = await progressNotifier.StartAsync(
                        "🔄 <b>Re-eval sweep</b> · verdict-improvement pass…", CancellationToken.None);
                    using var scope = scopeFactory.CreateScope();
                    var reeval = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Maintenance.ReevalService>();
                    var result = await reeval.RunAsync(progress, CancellationToken.None);
                    if (result.StoppedReason is { } reason)
                    {
                        await progress.CompleteAsync($"🔄 ⛔ {reason}", CancellationToken.None);
                        return;
                    }

                    await progress.CompleteAsync(
                        $"🔄 ✅ done · {result.QueuedJobIds.Count} queued · {result.WorthyIds.Count} more flagged",
                        CancellationToken.None);

                    if (result.WorthyIds.Count > 0)
                    {
                        await _bot!.SendMessage(
                            chatId: _adminChatId,
                            text: result.Html,
                            parseMode: ParseMode.Html,
                            replyMarkup: new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
                            [
                                [Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData(
                                    $"🔎 Research remaining {result.WorthyIds.Count}",
                                    $"rall|{string.Join(',', result.WorthyIds)}")],
                            ]),
                            linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                            cancellationToken: CancellationToken.None);
                    }
                    else
                    {
                        await notifier.SendAsync(result.Html, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Sweep failed");
                    await notifier.SendAsync("🔄 Sweep crashed — check logs.", CancellationToken.None);
                }
            },
            CancellationToken.None);

        return string.Empty; // the progress message is the reply
    }

    private string StartAudit()
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var audit = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Maintenance.AuditService>();
                    var result = await audit.RunAsync(CancellationToken.None);
                    if (result.UnresearchedIds.Count > 0)
                    {
                        await _bot!.SendMessage(
                            chatId: _adminChatId,
                            text: result.Html,
                            parseMode: ParseMode.Html,
                            replyMarkup: new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
                            [
                                [Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData(
                                    $"🔎 Research all {result.UnresearchedIds.Count}",
                                    $"rall|{string.Join(',', result.UnresearchedIds)}")],
                            ]),
                            linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                            cancellationToken: CancellationToken.None);
                    }
                    else
                    {
                        await notifier.SendAsync(result.Html, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Audit failed");
                    await notifier.SendAsync("🧾 Audit crashed — check logs.", CancellationToken.None);
                }
            },
            CancellationToken.None);

        return "🧾 Auditing the pipeline for leaks…";
    }

    private async Task<string> StartResearchAsync(string? argument, CancellationToken cancellationToken)
    {
        var ids = (argument ?? string.Empty)
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => long.TryParse(t, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .Take(10)
            .ToList();
        if (ids.Count == 0)
        {
            return "Usage: /research 5 — or several: /research 20 21 24";
        }

        var ack = await _bot!.SendMessage(
            chatId: _adminChatId, text: "🔎 queuing…", cancellationToken: cancellationToken);

        using var scope = scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
        var lines = new List<string>();
        foreach (var ideaId in ids)
        {
            var (jobId, position) = await jobs.EnqueueAsync(
                "research", new ResearchJobPayload(ideaId), ack.MessageId, cancellationToken);
            lines.Add($"🔎 job <b>#{jobId}</b> · pos {position} · idea #{ideaId} (/idea {ideaId})");
        }

        await _bot!.EditMessageText(
            chatId: _adminChatId,
            messageId: ack.MessageId,
            text: string.Join('\n', lines) +
                "\n<i>Progress logs reply to THIS message. /queue for overview.</i>",
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

    private static readonly string[] IdeaFilters = ["top", "all", "hot", "uncertain", "new", "fresh", "stale", "dead", "seen"];

    private async Task<string> SendIdeasPageAsync(string? argument, CancellationToken cancellationToken)
    {
        var filter = argument?.Trim().ToLowerInvariant() switch
        {
            "all" => "all",
            "hot" => "hot",
            "uncertain" => "uncertain",
            "new" => "new",
            "fresh" or "48h" => "fresh",
            "stale" or "outdated" => "stale",
            "dead" or "killed" => "dead",
            "seen" or "verified" => "seen",
            _ => "top",
        };

        var (text, keyboard) = await BuildIdeasPageAsync(filter, 1, DefaultSort(filter), DefaultGroup(filter), cancellationToken);
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
            if (parts.Length >= 2 && parts[0] == "appealb" && long.TryParse(parts[1], out var appealIdeaId))
            {
                StartAppeal(parts[1]);
                await _bot!.AnswerCallbackQuery(
                    callback.Id, $"⚖️ appeal #{appealIdeaId} running…", cancellationToken: cancellationToken);
                return;
            }

            if (parts.Length >= 2 && parts[0] == "partnerb" && long.TryParse(parts[1], out var partnerCbId))
            {
                StartPartner(parts[1]);
                await _bot!.AnswerCallbackQuery(
                    callback.Id, $"🤝 partner reading #{partnerCbId}…", cancellationToken: cancellationToken);
                return;
            }

            if (parts.Length >= 2 && parts[0] == "hand")
            {
                if (parts[1] == "apply")
                {
                    var outcome = await ExecuteHandWritesAsync(cancellationToken);
                    await _bot!.EditMessageText(
                        chatId: _adminChatId,
                        messageId: message.MessageId,
                        text: "🫱 <b>Applied</b>\n" + outcome,
                        parseMode: ParseMode.Html,
                        linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    using var handScope = scopeFactory.CreateScope();
                    var handDb = handScope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
                    var pendingRow = await handDb.AppState.FindAsync(["hand.pending"], cancellationToken);
                    if (pendingRow is not null)
                    {
                        handDb.AppState.Remove(pendingRow);
                        await handDb.SaveChangesAsync(cancellationToken);
                    }

                    await _bot!.EditMessageText(
                        chatId: _adminChatId,
                        messageId: message.MessageId,
                        text: "🫱 proposals discarded — nothing changed.",
                        linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                        cancellationToken: cancellationToken);
                }

                await _bot!.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
                return;
            }

            if (parts.Length >= 2 && parts[0] == "originb" && long.TryParse(parts[1], out _))
            {
                var originCard = await BuildOriginAsync(parts[1], cancellationToken);
                await ReplyAsync(originCard, cancellationToken);
                await _bot!.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
                return;
            }

            if (parts.Length >= 2 && parts[0] == "rall")
            {
                var ids = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => long.TryParse(t, out var id) ? id : 0)
                    .Where(id => id > 0)
                    .Take(10)
                    .ToList();
                using var scope = scopeFactory.CreateScope();
                var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
                foreach (var id in ids)
                {
                    await jobs.EnqueueAsync("research", new ResearchJobPayload(id), null, cancellationToken);
                }

                await _bot!.AnswerCallbackQuery(
                    callback.Id, $"🔎 queued {ids.Count} research job(s) — /queue", cancellationToken: cancellationToken);
                return;
            }

            if (parts.Length >= 2 && parts[0] is "verify" or "rr" or "promoteb" or "killb"
                && long.TryParse(parts[1], out var actIdeaId))
            {
                var answer = parts[0] switch
                {
                    "verify" => await VerifyIdeaAsync(parts[1], cancellationToken),
                    "rr" => await StartResearchAsync(parts[1], cancellationToken) is { Length: > 0 } usage
                        ? usage
                        : $"🔎 re-research queued for #{actIdeaId}",
                    "promoteb" => await SetIdeaStatusAsync(parts[1], "hot", cancellationToken),
                    _ => await SetIdeaStatusAsync(parts[1], "dismissed", cancellationToken),
                };
                await _bot!.AnswerCallbackQuery(
                    callback.Id,
                    System.Text.RegularExpressions.Regex.Replace(answer, "<[^>]+>", string.Empty)
                        is { Length: > 190 } longText ? longText[..190] : System.Text.RegularExpressions.Regex.Replace(answer, "<[^>]+>", string.Empty),
                    cancellationToken: cancellationToken);
                return;
            }

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
                var jobsService = scope.ServiceProvider.GetRequiredService<JobService>();
                var released = await jobsService.ReleaseHeldAsync(cancellationToken);
                if (long.TryParse(parts[2], out var bumpJobId) && bumpJobId > 0)
                {
                    await jobsService.RetryAsync(bumpJobId, cancellationToken);
                }

                await _bot!.AnswerCallbackQuery(
                    callback.Id, $"Caps +$5 (bump ${total:F0}) · {released} held job(s) resumed",
                    showAlert: true, cancellationToken: cancellationToken);
                await notifier.SendAsync(
                    $"💸 <b>+$5 for today</b> (total bump ${total:F0}) · ▶️ resumed {released} held job(s).",
                    cancellationToken);
                return;
            }

            if (parts.Length >= 2 && parts[0] == "job" && parts[1] == "releaseheld")
            {
                using var scope = scopeFactory.CreateScope();
                var released = await scope.ServiceProvider.GetRequiredService<JobService>()
                    .ReleaseHeldAsync(cancellationToken);
                await _bot!.AnswerCallbackQuery(
                    callback.Id, $"▶️ {released} held job(s) resumed (will re-hold if the cap is still gone)",
                    showAlert: true, cancellationToken: cancellationToken);
                return;
            }

            if (parts.Length >= 2 && parts[0] == "job" && parts[1] == "retryall")
            {
                using var scope = scopeFactory.CreateScope();
                var retried = await scope.ServiceProvider.GetRequiredService<JobService>()
                    .RetryAllFailedAsync(cancellationToken);
                await _bot!.AnswerCallbackQuery(
                    callback.Id, $"🔁 {retried} failed job(s) re-queued", cancellationToken: cancellationToken);
                return;
            }

            if (parts.Length >= 3 && parts[0] == "job" && parts[1] == "cancel"
                && long.TryParse(parts[2], out var cancelJobId))
            {
                using var scope = scopeFactory.CreateScope();
                var canceled = await scope.ServiceProvider.GetRequiredService<JobService>()
                    .CancelAsync(cancelJobId, cancellationToken);
                if (!canceled && RunningJobs.TryCancel(cancelJobId))
                {
                    canceled = true;
                }

                await _bot!.AnswerCallbackQuery(
                    callback.Id,
                    canceled ? $"✖️ job #{cancelJobId} cancelling" : "not cancelable (done/unknown)",
                    cancellationToken: cancellationToken);
                return;
            }

            if (parts.Length >= 5 && parts[0] == "sigs")
            {
                var sigPage = int.TryParse(parts[2], out var sp) ? sp : 1;
                var sigSort = parts[3] == "new" ? "new" : "value";
                var sigGroup = parts[4] == "1";
                var (sigText, sigKeyboard) = await BuildSignalsPageAsync(parts[1], sigPage, sigSort, sigGroup, cancellationToken);
                await _bot!.EditMessageText(
                    chatId: _adminChatId,
                    messageId: message.MessageId,
                    text: sigText,
                    parseMode: ParseMode.Html,
                    linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                    replyMarkup: sigKeyboard,
                    cancellationToken: cancellationToken);
                await _bot!.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
                return;
            }

            if (parts.Length >= 3 && parts[0] == "ideas")
            {
                var page = int.TryParse(parts[2], out var parsed) ? parsed : 1;
                var ideasSort = parts.Length >= 4 && parts[3] == "new" ? "new" : parts.Length >= 4 ? "score" : DefaultSort(parts[1]);
                var ideasGroup = parts.Length >= 5 ? parts[4] == "1" : DefaultGroup(parts[1]);
                var (text, keyboard) = await BuildIdeasPageAsync(parts[1], page, ideasSort, ideasGroup, cancellationToken);
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

    private static string DefaultSort(string filter) =>
        filter is "all" or "dead" ? "new" : "score";

    /// <summary>Timeline views start grouped by day; score views stay flat (owner's choice).</summary>
    private static bool DefaultGroup(string filter) => filter is "fresh" or "all";

    private async Task<(string Text, InlineKeyboardMarkup Keyboard)> BuildIdeasPageAsync(
        string filter, int page, string sort, bool group, CancellationToken cancellationToken)
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
                .Select(r => new { r.IdeaId, r.ReportJson, r.CreatedAt, r.EngineVersion })
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.IdeaId)
            .ToDictionary(g => g.Key, g => g.First());

        var scored = ideas
            .Select(i =>
            {
                var reportRow = latestReports.GetValueOrDefault(i.Id);
                var dto = reportRow is null ? null : IdeaJson.SafeDeserialize<ResearchReportDto>(reportRow.ReportJson);
                var stale = false;
                if (reportRow is not null)
                {
                    var (lastNoteAt, appealAt) = IdeaEngine.Infrastructure.Ideation.IdeaScores.JudgmentMoments(i);
                    stale = IdeaEngine.Core.Common.Staleness.IsStale(
                        reportRow.EngineVersion, reportRow.CreatedAt, lastNoteAt, appealAt);
                }

                return new
                {
                    Idea = i,
                    Score = IdeaJson.ComputeScore(i, dto, reportRow?.CreatedAt),
                    Stale = stale,
                };
            })
            .ToList();

        var cutoff48 = timeProvider.GetUtcNow().AddHours(-48);
        var selected = filter switch
        {
            // Default: live, unreviewed work. Verified and dead are noise here.
            "top" => scored.Where(x => !x.Idea.Verified && x.Idea.Status != "dismissed"),
            "hot" => scored.Where(x => x.Idea.Status == "hot" && !x.Idea.Verified),
            "uncertain" => scored.Where(x => x.Idea.Status is "uncertain" or "validated" && !x.Idea.Verified),
            "new" => scored.Where(x => x.Idea.Status == "candidate" && !x.Idea.Verified),
            "fresh" => scored.Where(x => x.Idea.CreatedAt >= cutoff48), // any status, incl. fresh kills
            "stale" => scored.Where(x => x.Stale && x.Idea.Status != "dismissed"),
            "dead" => scored.Where(x => x.Idea.Status == "dismissed"),
            "seen" => scored.Where(x => x.Idea.Verified),
            _ => scored,
        };

        var filtered = sort == "new"
            ? selected.OrderByDescending(x => x.Idea.Id).ToList()
            : selected.OrderByDescending(x => x.Score.Total).ThenByDescending(x => x.Idea.Id).ToList();

        var pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, pages);
        var slice = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var activeJobs = await db.Jobs.CountAsync(
            j => j.Status == "queued" || j.Status == "running", cancellationToken);

        var builder = new StringBuilder();
        builder.Append("<b>💡 Ideas · ").Append(FilterLabel(filter)).Append("</b> — ")
            .Append(filtered.Count).Append(" total · sorted by ").Append(sort == "new" ? "newest" : "score")
            .Append(" <i>(⭐ researched · ≈ estimate)</i>\n");
        if (activeJobs > 0)
        {
            builder.Append("⏳ ").Append(activeJobs).Append(" job(s) in flight — /queue\n");
        }

        builder.Append('\n');

        if (slice.Count == 0)
        {
            builder.Append("<i>nothing here yet</i>\n");
        }

        DateOnly? previousDay = null;
        foreach (var entry in slice)
        {
            if (group)
            {
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.Idea.CreatedAt, timeZone).Date);
                if (day != previousDay)
                {
                    previousDay = day;
                    builder.Append("— <i>").Append(DayLabel(day)).Append("</i> —\n");
                }
            }

            var id = ('#' + entry.Idea.Id.ToString(CultureInfo.InvariantCulture)).PadRight(4);
            var pct = ((entry.Score.Total * 100).ToString("F0", CultureInfo.InvariantCulture) + "%")
                .PadLeft(4) + (entry.Score.Source == "research" ? "⭐" : "≈");

            // Uncertain is a wide bucket: tier arrows make its inner spread scannable.
            var tier = entry.Idea.Status is "uncertain" or "validated"
                ? entry.Score.Total >= 0.55 ? "↑" : entry.Score.Total < 0.40 ? "↓" : "→"
                : string.Empty;

            builder.Append(IdeaEngine.Core.Common.Ui.IdeaStatus(entry.Idea.Status)).Append(tier)
                .Append(" <code>").Append(id).Append(pct).Append("</code> ")
                .Append(entry.Stale ? "⌛ " : string.Empty)
                .Append(entry.Idea.NotesJson is { Length: > 2 } ? "🗒 " : string.Empty)
                .Append(System.Net.WebUtility.HtmlEncode(entry.Idea.Title));
            if (entry.Idea.Origin == "operator")
            {
                builder.Append(" — <i>yours</i>");
            }

            builder.Append('\n');
        }

        if (slice.Count > 0)
        {
            var first = slice[0].Idea.Id;
            builder.Append("\n<i>tap on any id (shown for #").Append(first).Append("):</i> ")
                .Append(IdeaEngine.Core.Common.Ui.Cmd("idea", first)).Append(" · ")
                .Append(IdeaEngine.Core.Common.Ui.Cmd("origin", first)).Append(" · ")
                .Append(IdeaEngine.Core.Common.Ui.Cmd("research", first)).Append(" · ")
                .Append(IdeaEngine.Core.Common.Ui.Cmd("partner", first)).Append(" · ")
                .Append(IdeaEngine.Core.Common.Ui.Cmd("appeal", first));
        }

        var groupFlag = group ? 1 : 0;
        var filterRow = IdeaFilters
            .Select(f => InlineKeyboardButton.WithCallbackData(
                (f == filter ? "• " : string.Empty) + FilterButton(f), $"ideas|{f}|1|{DefaultSort(f)}|{(DefaultGroup(f) ? 1 : 0)}"))
            .ToArray();

        var navRow = new List<InlineKeyboardButton>();
        if (page > 1)
        {
            navRow.Add(InlineKeyboardButton.WithCallbackData("⬅️", $"ideas|{filter}|{page - 1}|{sort}|{groupFlag}"));
        }

        navRow.Add(InlineKeyboardButton.WithCallbackData($"{page}/{pages}", $"ideas|{filter}|{page}|{sort}|{groupFlag}"));
        if (page < pages)
        {
            navRow.Add(InlineKeyboardButton.WithCallbackData("➡️", $"ideas|{filter}|{page + 1}|{sort}|{groupFlag}"));
        }

        navRow.Add(sort == "new"
            ? InlineKeyboardButton.WithCallbackData("⭐ by score", $"ideas|{filter}|1|score|{groupFlag}")
            : InlineKeyboardButton.WithCallbackData("🕐 newest", $"ideas|{filter}|1|new|{groupFlag}"));
        navRow.Add(InlineKeyboardButton.WithCallbackData(
            group ? "📅 off" : "📅 group", $"ideas|{filter}|1|{sort}|{(group ? 0 : 1)}"));

        var keyboard = new InlineKeyboardMarkup(
        [
            filterRow[..5],
            filterRow[5..],
            [.. navRow],
        ]);

        return (builder.ToString(), keyboard);
    }

    private static string FilterLabel(string filter) => filter switch
    {
        "top" => "top unreviewed",
        "seen" => "✅ verified by you",
        "hot" => "🔥 hot",
        "uncertain" => "🤔 uncertain",
        "new" => "🌱 new",
        "fresh" => "🌊 last 48h, any status",
        "stale" => "⌛ stale research (re-run would matter)",
        "dead" => "☠️ killed",
        _ => "all by number",
    };

    private static string FilterButton(string filter) => filter switch
    {
        "top" => "Top",
        "seen" => "✅",
        "hot" => "🔥",
        "uncertain" => "🤔",
        "new" => "🌱",
        "fresh" => "🌊48h",
        "stale" => "⌛",
        "dead" => "☠️",
        _ => "All",
    };

    private async Task<string> CancelJobAsync(string? argument, CancellationToken cancellationToken)
    {
        if (!long.TryParse(argument, out var jobId))
        {
            return "Usage: /cancel 7 (job ids are shown by /queue; only queued/held jobs cancel — running ones finish)";
        }

        using var scope = scopeFactory.CreateScope();
        var canceled = await scope.ServiceProvider.GetRequiredService<JobService>()
            .CancelAsync(jobId, cancellationToken);
        if (canceled)
        {
            return $"✖️ Job <b>#{jobId}</b> canceled (was waiting).";
        }

        return RunningJobs.TryCancel(jobId)
            ? $"🛑 Cancelling RUNNING job <b>#{jobId}</b> — stops at the next step; spent tokens are lost."
            : $"Job #{jobId} isn't cancelable (done or unknown) — /queue for state.";
    }

    private async Task<string> SendQueueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var now = timeProvider.GetUtcNow();

        var active = await db.Jobs
            .Where(j => j.Status == "queued" || j.Status == "running")
            .OrderBy(j => j.Id)
            .ToListAsync(cancellationToken);
        var held = await db.Jobs
            .Where(j => j.Status == "held")
            .OrderBy(j => j.Id)
            .ToListAsync(cancellationToken);
        var failed = await db.Jobs
            .Where(j => j.Status == "failed")
            .OrderByDescending(j => j.Id)
            .Take(3)
            .ToListAsync(cancellationToken);
        var doneToday = await db.Jobs.CountAsync(
            j => j.Status == "done" && j.UpdatedAt >= now.AddHours(-24), cancellationToken);

        var builder = new StringBuilder("<b>📋 Queue</b> <i>(🏋️ heavy ∥ 🪶 light — lanes run in parallel)</i>\n");
        if (active.Count == 0)
        {
            builder.Append("😴 empty — nothing running or waiting\n");
        }

        var runningJob = active.FirstOrDefault(j => j.Status == "running");
        if (runningJob is { ProgressMessageId: not null })
        {
            builder.Append("<i>▶️ this message replies to the LIVE log of job #")
                .Append(runningJob.Id).Append(" — tap the quote above to jump</i>\n");
        }

        var position = 0;
        foreach (var job in active)
        {
            position++;
            var marker = job.Status == "running" ? "▶️" : $"{position}.";
            var lane = job.Kind is "appeal" or "partner" ? "🪶" : "🏋️";
            builder.Append(marker).Append(' ').Append(lane).Append(" <b>#").Append(job.Id).Append("</b> ")
                .Append(job.Kind).Append(' ').Append(JobLabel(job))
                .Append(job.Status == "running"
                    ? $" · running {(now - job.UpdatedAt).TotalMinutes:F0}m"
                    : string.Empty)
                .Append('\n');
        }

        if (held.Count > 0)
        {
            builder.Append("\n<b>⏸ Held (budget)</b> — auto-resume ");
            var until = held.Min(j => j.HoldUntil) ?? DateTimeOffset.UtcNow;
            builder.Append(TimeZoneInfo.ConvertTime(until, timeZone).ToString("HH:mm", CultureInfo.InvariantCulture))
                .Append(' ').Append(IdeaEngine.Core.Common.Scheduling.ZoneLabel(timeZone)).Append('\n');
            foreach (var job in held.Take(6))
            {
                builder.Append("• <b>#").Append(job.Id).Append("</b> ").Append(job.Kind)
                    .Append(' ').Append(JobLabel(job)).Append('\n');
            }
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

        var rows = new List<Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton[]>();
        var cancelable = active.Concat(held).Take(6)
            .Select(j => Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton
                .WithCallbackData($"✖️ #{j.Id}", $"job|cancel|{j.Id}"))
            .ToArray();
        if (cancelable.Length > 0)
        {
            rows.Add(cancelable);
        }

        var bulkRow = new List<Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton>();
        if (held.Count > 0)
        {
            bulkRow.Add(Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton
                .WithCallbackData($"💸 +$5 resume {held.Count}", "budget|bump|0"));
            bulkRow.Add(Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton
                .WithCallbackData("▶️ resume now", "job|releaseheld|0"));
        }

        if (failed.Count > 0)
        {
            bulkRow.Add(Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton
                .WithCallbackData("🔁 retry all failed", "job|retryall|0"));
        }

        if (bulkRow.Count > 0)
        {
            rows.Add([.. bulkRow]);
        }

        if (retryRow.Count > 0)
        {
            rows.Add([.. retryRow]);
        }

        await _bot!.SendMessage(
            chatId: _adminChatId,
            text: builder.ToString(),
            parseMode: ParseMode.Html,
            replyParameters: runningJob?.ProgressMessageId is { } liveLogId
                ? new Telegram.Bot.Types.ReplyParameters { MessageId = liveLogId }
                : null,
            replyMarkup: rows.Count > 0
                ? new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(rows)
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

            if (job.Kind == "dig")
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<DigJobPayload>(
                    job.PayloadJson, LlmJson.Options);
                return payload is null ? string.Empty : $"\u201c{IdeaEngine.Core.Common.TextClip.Clip(payload.Topic, 30)}\u201d";
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

    private async Task<string> ListNotesAsync(string? argument, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();

        if (long.TryParse(argument, out var ideaId))
        {
            var idea = await db.Ideas.FindAsync([ideaId], cancellationToken);
            if (idea is null)
            {
                return $"No idea #{ideaId}.";
            }

            var notes = IdeaJson.SafeDeserialize<List<Dictionary<string, string>>>(idea.NotesJson) ?? [];
            if (notes.Count == 0)
            {
                return $"#{ideaId} has no notes yet — /note{ideaId} your argument";
            }

            var one = new StringBuilder();
            one.Append("🗒 <b>Notes on #").Append(ideaId).Append(" · ")
                .Append(System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(idea.Title, 55)))
                .Append("</b>\n");
            foreach (var note in notes.TakeLast(10))
            {
                var when = note.TryGetValue("at", out var at)
                    && DateTimeOffset.TryParse(at, out var parsed)
                    ? TimeZoneInfo.ConvertTime(parsed, timeZone).ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)
                    : "?";
                one.Append("• <i>").Append(when).Append("</i> — ")
                    .Append(System.Net.WebUtility.HtmlEncode(note.GetValueOrDefault("text") ?? string.Empty))
                    .Append('\n');
            }

            one.Append("\n<i>").Append(IdeaEngine.Core.Common.Ui.Cmd("research", ideaId)).Append(" makes the judge address these</i>");
            return one.ToString();
        }

        var withNotes = await db.Ideas
            .Where(i => i.NotesJson != null && i.Category != "meta")
            .OrderByDescending(i => i.Id)
            .Take(30)
            .ToListAsync(cancellationToken);
        if (withNotes.Count == 0)
        {
            return "No notes anywhere yet — /note 5 your argument attaches one.";
        }

        var builder = new StringBuilder("🗒 <b>Ideas with your notes</b>\n\n");
        foreach (var idea in withNotes)
        {
            var notes = IdeaJson.SafeDeserialize<List<Dictionary<string, string>>>(idea.NotesJson) ?? [];
            if (notes.Count == 0)
            {
                continue;
            }

            builder.Append("<b>#").Append(idea.Id).Append("</b> (").Append(notes.Count).Append("🗒) ")
                .Append(System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(idea.Title, 45)))
                .Append("\n   <i>")
                .Append(System.Net.WebUtility.HtmlEncode(
                    IdeaEngine.Core.Common.TextClip.Clip(notes[^1].GetValueOrDefault("text") ?? string.Empty, 80)))
                .Append("</i>\n");
        }

        builder.Append("\n/notes 12 for full history · re-research to make them count");
        return builder.ToString();
    }

    private async Task<string> FindIdeasAsync(string? argument, CancellationToken cancellationToken)
    {
        var query = argument?.Trim();
        if (query is null || query.Length < 2)
        {
            return "Usage: /find lego sorting — fuzzy search over titles and theses (typos fine)";
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();

        // Stage 1: trigram similarity + substring, $0, typo-tolerant.
        var pattern = $"%{query}%";
        var hits = await db.Ideas
            .FromSqlInterpolated($@"
                SELECT * FROM ideas
                WHERE category <> 'meta' AND (
                    title ILIKE {pattern} OR thesis ILIKE {pattern}
                    OR similarity(title, {query}) > 0.18
                    OR similarity(coalesce(thesis, ''), {query}) > 0.12)
                ORDER BY GREATEST(similarity(title, {query}), similarity(coalesce(thesis, ''), {query})) DESC
                LIMIT 8")
            .ToListAsync(cancellationToken);

        // Stage 2: nano semantic fallback when the cheap pass finds nothing.
        if (hits.Count == 0)
        {
            var recent = await db.Ideas
                .Where(i => i.Category != "meta")
                .OrderByDescending(i => i.Id)
                .Take(60)
                .Select(i => new { i.Id, i.Title })
                .ToListAsync(cancellationToken);
            var chat = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Ai.OpenRouterChatClient>();
            var nano = scope.ServiceProvider
                .GetRequiredService<IOptions<IdeaEngine.Infrastructure.Ai.GlanceOptions>>().Value;
            var completion = await chat.CompleteAsync(
                nano.Model,
                "Match the query to idea titles semantically. Reply ONLY {\"ids\":[..]} with up to 5 matching ids, best first. Empty list when nothing fits.",
                $"query: {query}\n" + string.Join('\n', recent.Select(r => $"{r.Id}: {r.Title}")),
                400, "low", cancellationToken);
            var ids = IdeaEngine.Infrastructure.Ai.LlmJson
                .TryParse<FindIdsDto>(completion?.Content)?.Ids ?? [];
            if (ids.Count > 0)
            {
                hits = await db.Ideas.Where(i => ids.Contains(i.Id)).ToListAsync(cancellationToken);
                hits = [.. hits.OrderBy(h => ids.IndexOf(h.Id))];
            }
        }

        if (hits.Count == 0)
        {
            return $"🔍 Nothing matching “{System.Net.WebUtility.HtmlEncode(query)}” — /ideas all to browse.";
        }

        var builder = new StringBuilder("🔍 <b>Found</b>\n\n");
        foreach (var idea in hits)
        {
            builder.Append(IdeaEngine.Core.Common.Ui.IdeaStatus(idea.Status))
                .Append(" <b>#").Append(idea.Id).Append("</b> ")
                .Append(System.Net.WebUtility.HtmlEncode(idea.Title)).Append('\n');
        }

        builder.Append("\n<i>/idea5 style taps work everywhere</i>");
        return builder.ToString();
    }

    private sealed record FindIdsDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("ids")] List<long>? Ids);

    private async Task<string> AddNoteAsync(string? argument, CancellationToken cancellationToken)
    {
        var parts = (argument ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !long.TryParse(parts[0], out var ideaId))
        {
            return "Usage: /note 5 your argument — stored on the idea and injected into the next /research";
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var idea = await db.Ideas.FindAsync([ideaId], cancellationToken);
        if (idea is null)
        {
            return $"No idea #{ideaId}.";
        }

        var notes = IdeaJson.SafeDeserialize<List<Dictionary<string, object>>>(idea.NotesJson) ?? [];
        notes.Add(new Dictionary<string, object>
        {
            ["text"] = parts[1].Trim(),
            ["at"] = timeProvider.GetUtcNow(),
        });
        idea.NotesJson = System.Text.Json.JsonSerializer.Serialize(notes, LlmJson.Options);
        await db.SaveChangesAsync(cancellationToken);

        return $"🗒 Note added to #{ideaId} ({notes.Count} total). Injected into the next /research{ideaId} — the judge must address it. ⌛ the idea now counts as stale.";
    }


    private static List<long> ParseIdList(string? argument) =>
        [.. (argument ?? string.Empty)
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => long.TryParse(t, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .Take(10)]; 

    private string StartAppeal(string? argument) =>
        EnqueueLightJobs("appeal", "⚖️", argument,
            "Usage: /appeal 5 — or several: /appeal 55 56 58 (queued in order, light lane)");

    private string StartPartner(string? argument) =>
        EnqueueLightJobs("partner", "🤝", argument,
            "Usage: /partner 5 — or several: /partner 69 24 (queued in order, light lane)");

    /// <summary>Appeal/partner ride the LIGHT lane: durable, cancellable, never behind research.</summary>
    private string EnqueueLightJobs(string kind, string emoji, string? argument, string usage)
    {
        var ids = ParseIdList(argument);
        if (ids.Count == 0)
        {
            return usage;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var ack = await _bot!.SendMessage(
                        chatId: _adminChatId, text: $"{emoji} queuing…", cancellationToken: CancellationToken.None);
                    using var scope = scopeFactory.CreateScope();
                    var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
                    var lines = new List<string>();
                    foreach (var ideaId in ids)
                    {
                        object payload = kind == "appeal"
                            ? new AppealJobPayload(ideaId)
                            : new PartnerJobPayload(ideaId);
                        var (jobId, _) = await jobs.EnqueueAsync(kind, payload, ack.MessageId, CancellationToken.None);
                        lines.Add($"{emoji} job <b>#{jobId}</b> · idea #{ideaId} ({IdeaEngine.Core.Common.Ui.Cmd("idea", ideaId)})");
                    }

                    await _bot!.EditMessageText(
                        chatId: _adminChatId,
                        messageId: ack.MessageId,
                        text: string.Join('\n', lines) + "\n<i>light lane — runs alongside research. /queue for overview.</i>",
                        parseMode: ParseMode.Html,
                        cancellationToken: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Queueing {Kind} failed", kind);
                    await notifier.SendAsync($"{emoji} queueing failed — check logs.", CancellationToken.None);
                }
            },
            CancellationToken.None);
        return string.Empty;
    }

    private async Task<string> VerifyIdeaAsync(string? argument, CancellationToken cancellationToken)
    {
        var ids = ParseIdList(argument);
        if (ids.Count == 0)
        {
            return "Usage: /verify 5 — or several: /verify 5 6 7 (marks reviewed, hides from default list)";
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var lines = new List<string>();
        foreach (var ideaId in ids)
        {
            var idea = await db.Ideas.FindAsync([ideaId], cancellationToken);
            if (idea is null)
            {
                lines.Add($"⛔ no idea #{ideaId}");
                continue;
            }

            idea.Verified = true;
            lines.Add($"✅ #{ideaId} verified");
        }

        await db.SaveChangesAsync(cancellationToken);
        return string.Join('\n', lines) + "\n<i>hidden from the default list (see ✅ tab)</i>";
    }

    private async Task<string> BumpBudgetAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var total = await scope.ServiceProvider
            .GetRequiredService<IdeaEngine.Infrastructure.Ai.BudgetGuard>()
            .BumpTodayAsync(5m, cancellationToken);
        var released = await scope.ServiceProvider.GetRequiredService<JobService>()
            .ReleaseHeldAsync(cancellationToken);
        var effective = budgetOptions.Value.GlobalDailyUsdCap + total;
        return $"💸 <b>+$5 for today.</b> Global daily cap now <b>${effective:F0}</b> " +
            $"(${budgetOptions.Value.GlobalDailyUsdCap:F0} base + ${total:F0} bumped); stage caps rise too. " +
            $"▶️ resumed {released} held job(s). Monthly ceiling unchanged.";
    }

    private async Task<string> SetIdeaStatusAsync(
        string? argument, string newStatus, CancellationToken cancellationToken)
    {
        var ids = ParseIdList(argument);
        if (ids.Count == 0)
        {
            return $"Usage: /{(newStatus == "hot" ? "promote" : "kill")} 5 — or several ids space-separated";
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var lines = new List<string>();
        foreach (var ideaId in ids)
        {
            var idea = await db.Ideas.FindAsync([ideaId], cancellationToken);
            if (idea is null)
            {
                lines.Add($"⛔ no idea #{ideaId}");
                continue;
            }

            var previous = idea.Status;
            idea.Status = newStatus;
            lines.Add($"{IdeaEngine.Core.Common.Ui.IdeaStatus(newStatus)} #{ideaId} " +
                $"{System.Net.WebUtility.HtmlEncode(idea.Title.Length > 60 ? idea.Title[..59] + "…" : idea.Title)}" +
                $" — {previous} → <b>{newStatus}</b>");
        }

        await db.SaveChangesAsync(cancellationToken);
        return string.Join('\n', lines) + "\n<i>your call, recorded</i>";
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

    private async Task<string> SendIdeaDetailAsync(string? argument, CancellationToken cancellationToken)
    {
        var text = await BuildIdeaDetailAsync(argument, cancellationToken);
        if (!long.TryParse(argument, out var kbIdeaId) || text.StartsWith("No idea", StringComparison.Ordinal)
            || text.StartsWith("Usage", StringComparison.Ordinal))
        {
            return text;
        }

        await _bot!.SendMessage(
            chatId: _adminChatId,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
            [
                [
                    Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("✅ Verify", $"verify|{kbIdeaId}"),
                    Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("🔎 Research", $"rr|{kbIdeaId}"),
                    Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("⚖️ Appeal", $"appealb|{kbIdeaId}"),
                ],
                [
                    Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("🔥 Promote", $"promoteb|{kbIdeaId}"),
                    Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("☠️ Kill", $"killb|{kbIdeaId}"),
                ],
                [
                    Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("🤝 Partner take", $"partnerb|{kbIdeaId}"),
                    Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("🧿 Origin", $"originb|{kbIdeaId}"),
                ],
            ]),
            linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
            cancellationToken: cancellationToken);
        return string.Empty;
    }

    // ---------------- Right hand: chat over the database, code executes ----------------

    private async Task<string> HandleChatCommandAsync(string? argument, CancellationToken cancellationToken)
    {
        var text = argument?.Trim();
        if (string.Equals(text, "end", StringComparison.OrdinalIgnoreCase))
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Hand.HandService>()
                .ClearSessionAsync(cancellationToken);
            return "🫱 session cleared — next message starts fresh.";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "🫱 Just type without / to talk to your right hand about anything in the database.\n" +
                "It reads whatever it needs; changes only happen after you approve the audit card.\n" +
                "/chat end — forget the current conversation.";
        }

        await HandleHandMessageAsync(text, cancellationToken);
        return string.Empty;
    }

    private async Task HandleHandMessageAsync(string text, CancellationToken cancellationToken)
    {
        await _bot!.SendChatAction(_adminChatId, Telegram.Bot.Types.Enums.ChatAction.Typing, cancellationToken: cancellationToken);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var hand = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Hand.HandService>();
            var turn = await hand.TurnAsync(text, cancellationToken);
            if (turn.StoppedReason is { } reason)
            {
                await ReplyAsync($"🫱 ⛔ {reason}", cancellationToken);
                return;
            }

            if (turn.Say.Length > 0)
            {
                await ReplyAsync("🫱 " + System.Net.WebUtility.HtmlEncode(turn.Say), cancellationToken);
            }

            if (turn.Writes.Count > 0)
            {
                var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
                var pendingJson = System.Text.Json.JsonSerializer.Serialize(turn.Writes, LlmJson.Options);
                var state = await db.AppState.FindAsync(["hand.pending"], cancellationToken);
                if (state is null)
                {
                    db.AppState.Add(new IdeaEngine.Infrastructure.Persistence.Entities.AppStateEntity
                    {
                        Key = "hand.pending",
                        Value = pendingJson,
                        UpdatedAt = timeProvider.GetUtcNow(),
                    });
                }
                else
                {
                    state.Value = pendingJson;
                    state.UpdatedAt = timeProvider.GetUtcNow();
                }

                await db.SaveChangesAsync(cancellationToken);

                var audit = new StringBuilder("🫱 <b>Proposed changes</b> — nothing happens until you approve:\n");
                var n = 1;
                foreach (var write in turn.Writes)
                {
                    audit.Append(n++).Append(". ")
                        .Append(System.Net.WebUtility.HtmlEncode(IdeaEngine.Infrastructure.Hand.HandService.Describe(write)))
                        .Append('\n');
                }

                await _bot!.SendMessage(
                    chatId: _adminChatId,
                    text: audit.ToString(),
                    parseMode: ParseMode.Html,
                    replyMarkup: new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
                    [
                        [
                            Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("✅ Apply", "hand|apply"),
                            Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("❌ Cancel", "hand|cancel"),
                        ],
                    ]),
                    linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Hand message failed");
            await ReplyAsync("🫱 crashed — check logs.", cancellationToken);
        }
    }

    /// <summary>CODE executes approved writes - the brain never touches data (owner's law).</summary>
    private async Task<string> ExecuteHandWritesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var pending = await db.AppState.FindAsync(["hand.pending"], cancellationToken);
        if (pending is null)
        {
            return "nothing pending (already applied or cancelled)";
        }

        var writes = IdeaJson.SafeDeserialize<List<IdeaEngine.Infrastructure.Hand.HandWrite>>(pending.Value) ?? [];
        db.AppState.Remove(pending);
        await db.SaveChangesAsync(cancellationToken);

        var results = new StringBuilder();
        foreach (var write in writes)
        {
            try
            {
                results.Append(await ExecuteOneWriteAsync(scope.ServiceProvider, write, cancellationToken)).Append('\n');
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Hand write {Action} failed", write.Action);
                results.Append("⛔ ").Append(write.Action).Append(" crashed: ").Append(ex.GetType().Name).Append('\n');
            }
        }

        return results.ToString().TrimEnd();
    }

    private async Task<string> ExecuteOneWriteAsync(
        IServiceProvider services, IdeaEngine.Infrastructure.Hand.HandWrite write, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<IdeaEngineDbContext>();
        switch (write.Action)
        {
            case "set_status" when write.Id is { } id && write.Status is "dismissed" or "hot" or "candidate" or "uncertain":
            {
                var idea = await db.Ideas.FindAsync([id], cancellationToken);
                if (idea is null)
                {
                    return $"⛔ #{id} not found";
                }

                var old = idea.Status;
                idea.Status = write.Status;
                await db.SaveChangesAsync(cancellationToken);
                return $"✅ #{id}: {old} → {write.Status}";
            }

            case "add_note" when write.Id is { } noteId && write.Text is { Length: > 0 }:
            {
                var idea = await db.Ideas.FindAsync([noteId], cancellationToken);
                if (idea is null)
                {
                    return $"⛔ #{noteId} not found";
                }

                var notes = IdeaJson.SafeDeserialize<List<Dictionary<string, object>>>(idea.NotesJson) ?? [];
                notes.Add(new Dictionary<string, object>
                {
                    ["text"] = "🫱 " + write.Text.Trim(),
                    ["at"] = timeProvider.GetUtcNow(),
                });
                idea.NotesJson = System.Text.Json.JsonSerializer.Serialize(notes, LlmJson.Options);
                await db.SaveChangesAsync(cancellationToken);
                return $"✅ note added to #{noteId}";
            }

            case "queue_research":
            {
                var ids = (write.Ids ?? (write.Id is { } single ? [single] : new List<long>())).Take(10).ToList();
                if (ids.Count == 0)
                {
                    return "⛔ queue_research without ids";
                }

                var jobs = services.GetRequiredService<JobService>();
                foreach (var researchId in ids)
                {
                    await jobs.EnqueueAsync("research", new ResearchJobPayload(researchId), null, cancellationToken);
                }

                return $"✅ research queued for {string.Join(", ", ids.Select(i => $"#{i}"))} — /queue";
            }

            case "run_appeal" when write.Id is { } appealId:
                StartAppeal(appealId.ToString(CultureInfo.InvariantCulture));
                return $"✅ appeal started on #{appealId}";

            case "run_partner" when write.Id is { } partnerId:
                StartPartner(partnerId.ToString(CultureInfo.InvariantCulture));
                return $"✅ partner started on #{partnerId}";

            case "set_model" when write.Stage is { Length: > 0 } && write.Model is { Length: > 0 }:
            {
                if (!ModelStages.Any(s => s.Stage == write.Stage))
                {
                    return $"⛔ unknown stage '{write.Stage}'";
                }

                var chat = services.GetRequiredService<IdeaEngine.Infrastructure.Ai.OpenRouterChatClient>();
                var ping = await chat.CompleteAsync(write.Model, "Reply with OK.", "ping", 16, "low", cancellationToken);
                if (ping is null || ping.IsError)
                {
                    return $"⛔ {write.Model} failed the live ping — not set";
                }

                if (!IdeaEngine.Infrastructure.Ai.ModelRegistry.KnownPrices.ContainsKey(write.Model))
                {
                    return $"⛔ unknown prices for {write.Model} — set via /models set {write.Stage} {write.Model} <$in> <$out>";
                }

                await services.GetRequiredService<IdeaEngine.Infrastructure.Ai.ModelRegistry>()
                    .SetAsync(write.Stage, write.Model, null, null, cancellationToken);
                return $"✅ {write.Stage} → {write.Model}";
            }

            case "set_setting" when write.Key is { Length: > 0 } && write.Value is { Length: > 0 }
                && IdeaEngine.Infrastructure.Autopilot.SettingsCatalog.Find(write.Key) is { } spec:
            {
                if (IdeaEngine.Infrastructure.Autopilot.SettingsCatalog.Validate(spec, write.Value) is { } problem)
                {
                    return $"⛔ {write.Key}: {problem} (allowed: {spec.Allowed})";
                }

                var key = "setting." + write.Key;
                var state = await db.AppState.FindAsync([key], cancellationToken);
                if (state is null)
                {
                    db.AppState.Add(new IdeaEngine.Infrastructure.Persistence.Entities.AppStateEntity
                    {
                        Key = key,
                        Value = write.Value,
                        UpdatedAt = timeProvider.GetUtcNow(),
                    });
                }
                else
                {
                    state.Value = write.Value;
                    state.UpdatedAt = timeProvider.GetUtcNow();
                }

                await db.SaveChangesAsync(cancellationToken);
                return $"✅ {write.Key} = {write.Value} (live from the next autopilot run)";
            }

            default:
                return $"⏭ skipped unsupported/incomplete action '{write.Action}'";
        }
    }

    // ---------------- MINE: AI memory as a source ----------------

    private string HandleMineCommand(string? argument)
    {
        var arg = argument?.Trim();
        if (string.Equals(arg, "list", StringComparison.OrdinalIgnoreCase))
        {
            var list = new StringBuilder("<b>⛏ Mine angles</b> — /mine rotates them; /mine &lt;your text&gt; digs YOUR fantasy:\n\n");
            foreach (var (key, prompt) in IdeaEngine.Infrastructure.Mine.MineService.Angles)
            {
                list.Append("• <b>").Append(key).Append("</b> — <i>")
                    .Append(System.Net.WebUtility.HtmlEncode(prompt)).Append("</i>\n");
            }

            list.Append("\nreply to any MINE card to keep digging that thread");
            return list.ToString();
        }

        StartMine(string.IsNullOrWhiteSpace(arg) ? null : arg, continuation: false);
        return "⛏ mining…";
    }

    private void StartMine(string? seed, bool continuation)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();

                    List<(string Role, string Content)>? history = null;
                    if (continuation)
                    {
                        var session = await db.AppState.FindAsync(["mine.session"], CancellationToken.None);
                        var lines = session is null
                            ? []
                            : IdeaJson.SafeDeserialize<List<Dictionary<string, string>>>(session.Value) ?? [];
                        history = [.. lines.Select(l => (l.GetValueOrDefault("role", "operator"), l.GetValueOrDefault("content", string.Empty)))];
                    }

                    var mine = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Mine.MineService>();
                    var result = await mine.RunAsync(seed, history, CancellationToken.None);
                    if (result.StoppedReason is { } reason)
                    {
                        await notifier.SendAsync($"⛏ mine ⛔ {reason}", CancellationToken.None);
                        return;
                    }

                    await notifier.SendAsync(result.Html, CancellationToken.None);

                    // Session memory for reply-continuations (last 10 lines).
                    var updated = (history ?? [])
                        .Append(("operator", seed ?? "(rotated angle)"))
                        .Append(("mine", System.Text.RegularExpressions.Regex.Replace(result.Html, "<[^>]+>", string.Empty)))
                        .TakeLast(10)
                        .Select(l => new Dictionary<string, string> { ["role"] = l.Item1, ["content"] = IdeaEngine.Core.Common.TextClip.Clip(l.Item2, 1200) })
                        .ToList();
                    var stateRow = await db.AppState.FindAsync(["mine.session"], CancellationToken.None);
                    var json = System.Text.Json.JsonSerializer.Serialize(updated, LlmJson.Options);
                    if (stateRow is null)
                    {
                        db.AppState.Add(new IdeaEngine.Infrastructure.Persistence.Entities.AppStateEntity
                        {
                            Key = "mine.session",
                            Value = json,
                            UpdatedAt = timeProvider.GetUtcNow(),
                        });
                    }
                    else
                    {
                        stateRow.Value = json;
                        stateRow.UpdatedAt = timeProvider.GetUtcNow();
                    }

                    await db.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Mine run failed");
                    await notifier.SendAsync("⛏ mine crashed — check logs.", CancellationToken.None);
                }
            },
            CancellationToken.None);
    }

    /// <summary>The idea at its best: verbatim pitch + every argument IN FAVOR. No concern walls.</summary>
    private async Task<string> BuildOriginAsync(string? argument, CancellationToken cancellationToken)
    {
        if (!long.TryParse(argument?.Trim(), out var ideaId))
        {
            return "Usage: /origin 5 — your original pitch and the optimistic composite";
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var idea = await db.Ideas.FindAsync([ideaId], cancellationToken);
        if (idea is null)
        {
            return $"No idea #{ideaId}.";
        }

        var builder = new StringBuilder();
        builder.Append("🧿 <b>Origin #").Append(idea.Id).Append(" · ")
            .Append(System.Net.WebUtility.HtmlEncode(idea.Title)).Append("</b>\n");

        if (idea.OriginalPitch is { Length: > 0 })
        {
            builder.Append("\n📝 <b>Your pitch, verbatim</b>\n<i>")
                .Append(System.Net.WebUtility.HtmlEncode(idea.OriginalPitch)).Append("</i>\n");
        }

        builder.Append("\n💡 ").Append(System.Net.WebUtility.HtmlEncode(idea.Thesis)).Append('\n');

        var variants = IdeaJson.SafeDeserialize<List<string>>(idea.VariantsJson);
        if (variants is { Count: > 0 })
        {
            builder.Append("\n🔀 <b>Variants</b>\n");
            foreach (var variant in variants)
            {
                builder.Append("• ").Append(System.Net.WebUtility.HtmlEncode(variant)).Append('\n');
            }
        }

        var originNotes = IdeaJson.SafeDeserialize<List<Dictionary<string, string>>>(idea.NotesJson);
        if (originNotes is { Count: > 0 })
        {
            builder.Append("\n🗒 <b>Your notes</b>\n");
            foreach (var note in originNotes)
            {
                if (note.TryGetValue("text", out var noteText))
                {
                    builder.Append("• ").Append(System.Net.WebUtility.HtmlEncode(noteText)).Append('\n');
                }
            }
        }

        // The advocate's best material survives in artifacts - resurface it here.
        var advocateArtifact = await db.ResearchArtifacts
            .Where(a => a.IdeaId == ideaId && a.Kind == "advocate")
            .OrderByDescending(a => a.Id)
            .Select(a => a.Json)
            .FirstOrDefaultAsync(cancellationToken);
        if (advocateArtifact is not null
            && IdeaJson.SafeDeserialize<Dictionary<string, string>>(advocateArtifact) is { } wrapper
            && wrapper.TryGetValue("raw", out var advocateRaw)
            && LlmJson.TryParse<OriginAdvocateDto>(advocateRaw) is { } advocate)
        {
            builder.Append("\n🛡 <b>Advocate's case</b>\n");
            if (advocate.CaseFor is { Length: > 0 })
            {
                builder.Append(System.Net.WebUtility.HtmlEncode(advocate.CaseFor)).Append('\n');
            }

            if (advocate.StrongestSingleArgument is { Length: > 0 })
            {
                builder.Append("💪 <i>").Append(System.Net.WebUtility.HtmlEncode(advocate.StrongestSingleArgument))
                    .Append("</i>\n");
            }

            foreach (var pivot in advocate.Pivots ?? [])
            {
                if (pivot.Name is { Length: > 0 })
                {
                    builder.Append("↪️ <b>").Append(System.Net.WebUtility.HtmlEncode(pivot.Name)).Append("</b>: ")
                        .Append(System.Net.WebUtility.HtmlEncode(pivot.What ?? string.Empty)).Append('\n');
                }
            }
        }

        var appealMeta = IdeaJson.SafeDeserialize<OriginAppealDto>(idea.AppealJson);
        if (appealMeta is not null)
        {
            if (appealMeta.Missed is { Count: > 0 })
            {
                builder.Append("\n⚖️ <b>The court noticed in your favor</b>\n");
                foreach (var missed in appealMeta.Missed)
                {
                    builder.Append("• ").Append(System.Net.WebUtility.HtmlEncode(missed)).Append('\n');
                }
            }

            if (appealMeta.WhatWouldMoveIt is { Length: > 0 }
                && !appealMeta.WhatWouldMoveIt.StartsWith("nothing", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append("🧭 <b>What would move it:</b> ")
                    .Append(System.Net.WebUtility.HtmlEncode(appealMeta.WhatWouldMoveIt)).Append('\n');
            }
        }

        var relatedList = IdeaJson.SafeDeserialize<List<Dictionary<string, System.Text.Json.JsonElement>>>(idea.RelatedJson);
        if (relatedList is { Count: > 0 })
        {
            builder.Append("\n🧬 related: ").Append(string.Join(" · ", relatedList
                .Select(r => r.TryGetValue("id", out var idEl) ? IdeaEngine.Core.Common.Ui.Cmd("idea", idEl.GetInt64()) : null)
                .Where(x => x is not null))).Append('\n');
        }

        builder.Append("\n<i>full trail with concerns: ")
            .Append(IdeaEngine.Core.Common.Ui.Cmd("idea", idea.Id)).Append("</i>");
        return builder.ToString();
    }

    private sealed record OriginAdvocateDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("case_for")] string? CaseFor,
        [property: System.Text.Json.Serialization.JsonPropertyName("strongest_single_argument")] string? StrongestSingleArgument,
        [property: System.Text.Json.Serialization.JsonPropertyName("pivots")] List<OriginPivotDto>? Pivots);

    private sealed record OriginPivotDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("what")] string? What);

    private sealed record OriginAppealDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("missed")] List<string>? Missed,
        [property: System.Text.Json.Serialization.JsonPropertyName("what_would_move_it")] string? WhatWouldMoveIt);

    private static readonly (string Stage, string Emoji, string Why)[] ModelStages =
    [
        ("triage", "🧹", "scores raw posts for pain; cheap, high volume"),
        ("glance", "👁", "10-word signal summaries"),
        ("builder", "🏗", "turns signals into structured ideas"),
        ("skeptic", "🥊", "attacks every idea; different vendor avoids self-agreement"),
        ("relate", "🧬", "links duplicate/variant ideas"),
        ("research", "🔎", "plans, searches, judges with web evidence"),
        ("repair", "🩹", "re-emits broken JSON from bigger models"),
        ("dig", "⛏", "operator-directed topic excavation"),
        ("appeal", "⚖️", "reviews the judge; strongest reasoning seat"),
        ("reeval", "♻️", "sweep screening of killed ideas"),
        ("audit", "🕵️", "weekly leak check"),
        ("partner", "🤝", "your right-seat take on ideas"),
    ];

    private async Task<string> HandleModelsAsync(string? argument, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Ai.ModelRegistry>();
        var tokens = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length >= 2 && tokens[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            var stage = tokens[1].ToLowerInvariant();
            return await registry.ResetAsync(stage, cancellationToken)
                ? $"↩️ {stage} back to its configured default."
                : $"{stage} had no override.";
        }

        if (tokens.Length >= 1 && tokens[0].Equals("available", StringComparison.OrdinalIgnoreCase))
        {
            var chatClient = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Ai.OpenRouterChatClient>();
            var catalog = await chatClient.ListModelsAsync(cancellationToken);
            if (catalog is null)
            {
                return "⛔ couldn't fetch the OpenRouter catalog — try again shortly.";
            }

            var search = tokens.Length > 1 ? string.Join(' ', tokens[1..]) : null;
            var hits = catalog
                .Where(m => search is null || m.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Take(25)
                .ToList();
            if (hits.Count == 0)
            {
                return $"nothing in the catalog matches “{search}” ({catalog.Count} models total)";
            }

            var list = new StringBuilder("<b>🌐 OpenRouter catalog</b> <i>($ per MTok in/out · live, cached 1h)</i>\n");
            foreach (var m in hits)
            {
                list.Append("<code>").Append(m.Id).Append("</code> $")
                    .Append(m.InPerMTok.ToString("0.##", CultureInfo.InvariantCulture)).Append('/')
                    .Append(m.OutPerMTok.ToString("0.##", CultureInfo.InvariantCulture));
                if (m.ContextLength is { } ctx)
                {
                    list.Append(" · ").Append(ctx / 1000).Append("k ctx");
                }

                list.Append('\n');
            }

            list.Append("\n<i>/models set &lt;stage&gt; &lt;id&gt; — prices autofill from this catalog</i>");
            return list.ToString();
        }

        if (tokens.Length >= 3 && tokens[0].Equals("effort", StringComparison.OrdinalIgnoreCase))
        {
            var stage = tokens[1].ToLowerInvariant();
            var level = tokens[2].ToLowerInvariant();
            if (!ModelStages.Any(s => s.Stage == stage))
            {
                return $"Unknown stage '{stage}'. Stages: {string.Join(", ", ModelStages.Select(s => s.Stage))}";
            }

            if (level is not ("minimal" or "low" or "medium" or "high"))
            {
                return "Effort levels: minimal · low · medium · high (or /models effort <stage> default)";
            }

            await registry.SetEffortAsync(stage, level, cancellationToken);
            return $"🧠 <b>{stage}</b> reasoning effort → <b>{level}</b> — live from the next run.";
        }

        if (tokens.Length >= 3 && tokens[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            var stage = tokens[1].ToLowerInvariant();
            if (!ModelStages.Any(s => s.Stage == stage))
            {
                return $"Unknown stage '{stage}'. Stages: {string.Join(", ", ModelStages.Select(s => s.Stage))}";
            }

            var model = tokens[2];
            decimal? inPrice = tokens.Length > 3 && decimal.TryParse(tokens[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var ip) ? ip : null;
            decimal? outPrice = tokens.Length > 4 && decimal.TryParse(tokens[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var op) ? op : null;
            if (inPrice is null && !IdeaEngine.Infrastructure.Ai.ModelRegistry.KnownPrices.ContainsKey(model))
            {
                // Autofill from the live catalog - the owner should never need the docs.
                var liveCatalog = await scope.ServiceProvider
                    .GetRequiredService<IdeaEngine.Infrastructure.Ai.OpenRouterChatClient>()
                    .ListModelsAsync(cancellationToken);
                var live = liveCatalog?.FirstOrDefault(m => m.Id.Equals(model, StringComparison.OrdinalIgnoreCase));
                if (live is not null)
                {
                    (inPrice, outPrice) = (live.InPerMTok, live.OutPerMTok);
                }
                else
                {
                    return $"{model} isn't in the live catalog — check /models available {model.Split('/')[^1]} " +
                        $"or pass prices: /models set {stage} {model} <$in/MTok> <$out/MTok>";
                }
            }

            // Live validation: a 1-token ping proves the id exists before money depends on it.
            var chat = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Ai.OpenRouterChatClient>();
            var ping = await chat.CompleteAsync(model, "Reply with OK.", "ping", 16, "low", cancellationToken);
            if (ping is null || ping.IsError)
            {
                return $"⛔ {model} failed the live ping ({IdeaEngine.Infrastructure.Ai.LlmDiag.Describe(ping)}) — not saved.";
            }

            await registry.SetAsync(stage, model, inPrice, outPrice, cancellationToken);
            var effective = await registry.ResolveAsync(stage, model, 0, 0, cancellationToken);
            return $"✅ <b>{stage}</b> → <code>{model}</code> (${effective.InPerMTok}/{effective.OutPerMTok} per MTok) — live from the next run. /models to review.";
        }

        var builder = new StringBuilder("<b>🧠 Models by stage</b> <i>(⚙️ = your override)</i>\n\n");
        foreach (var (stage, emoji, why) in ModelStages)
        {
            var (defModel, defIn, defOut) = StageDefaults(scope.ServiceProvider, stage);
            var resolved = await registry.ResolveAsync(stage, defModel, defIn, defOut, cancellationToken);
            builder.Append(emoji).Append(" <b>").Append(stage).Append("</b> — <code>")
                .Append(resolved.Model).Append("</code> $").Append(resolved.InPerMTok)
                .Append('/').Append(resolved.OutPerMTok)
                .Append(resolved.Overridden ? " ⚙️" : string.Empty)
                .Append(resolved.Effort is { Length: > 0 } ? $" · 🧠{resolved.Effort}" : string.Empty)
                .Append("\n<i>").Append(why).Append("</i>\n");
        }

        builder.Append("\n/models set &lt;stage&gt; &lt;id&gt; [$in] [$out] · /models effort &lt;stage&gt; &lt;level&gt; · /models reset &lt;stage&gt;")
            .Append("\n/models available [search] — the live OpenRouter catalog with prices")
            .Append("\n<i>prices autofill from the catalog; a live ping validates before saving</i>");
        return builder.ToString();
    }

    private static (string Model, decimal In, decimal Out) StageDefaults(IServiceProvider services, string stage)
    {
        var ideation = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdeaEngine.Infrastructure.Ai.IdeationOptions>>().Value;
        var research = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdeaEngine.Infrastructure.Research.ResearchOptions>>().Value;
        var nano = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdeaEngine.Infrastructure.Ai.GlanceOptions>>().Value;
        var triage = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdeaEngine.Infrastructure.Ai.TriageOptions>>().Value;
        var appeal = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdeaEngine.Infrastructure.Research.AppealOptions>>().Value;
        var dig = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdeaEngine.Infrastructure.Research.DigOptions>>().Value;
        var partner = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdeaEngine.Infrastructure.Research.PartnerOptions>>().Value;
        return stage switch
        {
            "triage" => (triage.Model, triage.InputPricePerMTok, triage.OutputPricePerMTok),
            "glance" or "relate" or "reeval" or "audit" => (nano.Model, nano.InputPricePerMTok, nano.OutputPricePerMTok),
            "builder" => (ideation.BuilderModel, ideation.BuilderInputPricePerMTok, ideation.BuilderOutputPricePerMTok),
            "skeptic" => (ideation.SkepticModel, ideation.SkepticInputPricePerMTok, ideation.SkepticOutputPricePerMTok),
            "research" => (research.Model, research.InputPricePerMTok, research.OutputPricePerMTok),
            "repair" => (research.RepairModel, research.RepairInputPricePerMTok, research.RepairOutputPricePerMTok),
            "dig" => (dig.Model, dig.InputPricePerMTok, dig.OutputPricePerMTok),
            "appeal" => (appeal.Model, appeal.InputPricePerMTok, appeal.OutputPricePerMTok),
            "partner" => (partner.Model, partner.InputPricePerMTok, partner.OutputPricePerMTok),
            _ => ("openai/gpt-5-nano", 0.05m, 0.40m),
        };
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
            .Select(r => new { r.Verdict, r.Confidence, r.SearchesUsed, r.CostUsd, r.CreatedAt, r.ReportJson, r.EngineVersion })
            .FirstOrDefaultAsync(cancellationToken);
        var researchReport = lastResearch is null
            ? null
            : IdeaJson.SafeDeserialize<IdeaEngine.Infrastructure.Research.ResearchReportDto>(lastResearch.ReportJson);

        var builder = new StringBuilder();
        builder.Append(IdeaEngine.Core.Common.Ui.IdeaStatus(idea.Status)).Append(" <b>#").Append(idea.Id).Append(" · ")
            .Append(System.Net.WebUtility.HtmlEncode(idea.Title)).Append("</b>\n")
            .Append(idea.Status).Append(" · ")
            .Append(idea.Category).Append(" · effort ").Append(idea.EffortScale)
            .Append(idea.Playbook is { Length: > 0 } ? $" · 📚 {idea.Playbook}" : string.Empty)
            .Append('\n');

        var score = IdeaJson.ComputeScore(idea, researchReport, lastResearch?.CreatedAt);
        if (score.Source != "none")
        {
            builder.Append("\n⭐ <b>Score ")
                .Append((score.Total * 100).ToString("F0", CultureInfo.InvariantCulture))
                .Append("%</b> — ")
                .Append(score.Source == "research" ? "from web research" : "skeptic estimate only")
                .Append(score.AppealAdjusted ? " · ⚖️ <i>appeal-corrected</i>" : string.Empty)
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

        // ---- Lineage: where this idea came from (glass pipeline). ----
        builder.Append("\n<b>🧬 Born from</b>\n");
        if (idea.Origin == "operator")
        {
            builder.Append("🧑 your pitch — ").Append(IdeaEngine.Core.Common.Ui.Cmd("origin", idea.Id))
                .Append(" shows it verbatim\n");
        }
        else if (idea.Origin == "dig")
        {
            builder.Append("⛏ /dig excavation").Append(idea.Playbook is { Length: > 0 } ? $" · 📚 {idea.Playbook}" : string.Empty).Append('\n');
        }
        else
        {
            builder.Append("🤖 ideation session").Append(idea.Playbook is { Length: > 0 } ? $" · 📚 {idea.Playbook}" : string.Empty).Append('\n');
        }

        var lineageIds = IdeaJson.ParseEvidence(idea.EvidenceJson);
        if (lineageIds.Count > 0)
        {
            var evidenceSignals = await db.Signals
                .Where(s => lineageIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Glance, s.Summary })
                .Take(5)
                .ToListAsync(cancellationToken);
            foreach (var ev in evidenceSignals)
            {
                builder.Append("⛓ ").Append(IdeaEngine.Core.Common.Ui.Cmd("signal", ev.Id)).Append(' ')
                    .Append(System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(
                        ev.Glance is { Length: > 0 } ? ev.Glance : ev.Summary, 70)))
                    .Append('\n');
            }
        }

        var cardNotes = IdeaJson.SafeDeserialize<List<Dictionary<string, string>>>(idea.NotesJson);
        if (cardNotes is { Count: > 0 })
        {
            builder.Append("\n<b>🗒 Your notes</b>\n");
            foreach (var note in cardNotes.TakeLast(3))
            {
                if (note.TryGetValue("text", out var noteText))
                {
                    builder.Append("• ").Append(System.Net.WebUtility.HtmlEncode(
                        IdeaEngine.Core.Common.TextClip.Clip(noteText, 120))).Append('\n');
                }
            }
        }

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

            var ledger = researchReport?.Concerns ?? [];
            if (ledger.Count > 0)
            {
                var fatal = ledger.Count(c => c.Status?.ToLowerInvariant() == "fatal");
                var openConcerns = ledger.Count(c => c.Status?.ToLowerInvariant() == "open");
                var mitigated = ledger.Count(c => c.Status?.ToLowerInvariant() == "mitigated");
                builder.Append("🧾 concerns: ");
                if (fatal > 0)
                {
                    builder.Append("🔥").Append(fatal).Append(" fatal · ");
                }

                builder.Append("🔓").Append(openConcerns).Append(" open · ✅")
                    .Append(mitigated).Append(" mitigated\n");
                foreach (var blocking in ledger
                    .Where(c => c.Status?.ToLowerInvariant() is "fatal" or "open").Take(3))
                {
                    builder.Append(IdeaEngine.Core.Common.Ui.ConcernStatus(blocking.Status)).Append(' ')
                        .Append(System.Net.WebUtility.HtmlEncode(
                            IdeaEngine.Core.Common.TextClip.Clip(blocking.Text ?? string.Empty, 160))).Append('\n');
                }
            }

            var (cardNoteAt, cardAppealAt) = IdeaEngine.Infrastructure.Ideation.IdeaScores.JudgmentMoments(idea);
            if (IdeaEngine.Core.Common.Staleness.IsStale(
                lastResearch.EngineVersion, lastResearch.CreatedAt, cardNoteAt, cardAppealAt))
            {
                builder.Append("⌛ <i>research predates newer reasoning or your latest arguments — a re-run would matter · ")
                    .Append(IdeaEngine.Core.Common.Ui.Cmd("research", idea.Id)).Append("</i>\n");
            }

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
                    .Append(" · rerun ").Append(IdeaEngine.Core.Common.Ui.Cmd("research", idea.Id)).Append("</i>\n");
            }
        }
        else
        {
            builder.Append("2️⃣ 🔎 <b>Web research</b> — not run yet → ").Append(IdeaEngine.Core.Common.Ui.Cmd("research", idea.Id)).Append('\n');
            if (skeptic?.ResearchQuestions is { Count: > 0 } questions)
            {
                builder.Append("<b>❓ It would investigate</b>\n");
                foreach (var question in questions.Take(5))
                {
                    builder.Append("🔹 ").Append(System.Net.WebUtility.HtmlEncode(question)).Append('\n');
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

        var relations = IdeaJson.SafeDeserialize<List<IdeaEngine.Infrastructure.Ideation.IdeaRelation>>(idea.RelatedJson);
        if (relations is { Count: > 0 })
        {
            builder.Append("\n<b>🧬 Related</b>\n");
            foreach (var relation in relations.Take(5))
            {
                builder.Append("• #").Append(relation.Id).Append(" <i>(").Append(relation.Kind)
                    .Append(")</i> — /idea ").Append(relation.Id).Append('\n');
            }
        }

        builder.Append("\n<i>argue: ").Append(IdeaEngine.Core.Common.Ui.Cmd("note", idea.Id)).Append(" your point · then 🔎 re-research · ")
            .Append(IdeaEngine.Core.Common.Ui.Cmd("origin", idea.Id)).Append(" for the bright side</i>");

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




    private async Task<string> HandleConfigAsync(string? argument, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();
        var autopilot = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<IdeaEngine.Infrastructure.Autopilot.AutopilotOptions>>().Value;
        var tokens = (argument ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length >= 2 && tokens[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            var spec = IdeaEngine.Infrastructure.Autopilot.SettingsCatalog.Find(tokens[1]);
            if (spec is null)
            {
                return $"Unknown setting '{tokens[1]}' — /config lists them.";
            }

            var row = await db.AppState.FindAsync(["setting." + spec.Key], cancellationToken);
            if (row is null)
            {
                return $"{spec.Key} had no override (already at default {spec.DefaultValue(autopilot)}).";
            }

            db.AppState.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
            return $"↩️ {spec.Key} back to default <b>{spec.DefaultValue(autopilot)}</b>.";
        }

        if (tokens.Length >= 3 && tokens[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            var spec = IdeaEngine.Infrastructure.Autopilot.SettingsCatalog.Find(tokens[1]);
            if (spec is null)
            {
                return $"Unknown setting '{tokens[1]}' — /config lists them.";
            }

            var value = tokens[2];
            if (IdeaEngine.Infrastructure.Autopilot.SettingsCatalog.Validate(spec, value) is { } problem)
            {
                return $"⛔ {spec.Key}: {problem} (allowed: {spec.Allowed})";
            }

            var key = "setting." + spec.Key;
            var row = await db.AppState.FindAsync([key], cancellationToken);
            if (row is null)
            {
                db.AppState.Add(new IdeaEngine.Infrastructure.Persistence.Entities.AppStateEntity
                {
                    Key = key,
                    Value = value,
                    UpdatedAt = timeProvider.GetUtcNow(),
                });
            }
            else
            {
                row.Value = value;
                row.UpdatedAt = timeProvider.GetUtcNow();
            }

            await db.SaveChangesAsync(cancellationToken);
            return $"✅ <b>{spec.Key}</b> = {value} — live from the next autopilot pass, no restart.";
        }

        var builder = new StringBuilder("<b>⚙️ Settings</b> <i>(runtime-adjustable; ⚙️ = your override)</i>\n\n");
        foreach (var spec in IdeaEngine.Infrastructure.Autopilot.SettingsCatalog.All)
        {
            var row = await db.AppState.FindAsync(["setting." + spec.Key], cancellationToken);
            var current = row?.Value ?? spec.DefaultValue(autopilot);
            builder.Append("<b>").Append(spec.Key).Append("</b> = ").Append(current)
                .Append(row is not null ? " ⚙️" : string.Empty)
                .Append(" <i>(default ").Append(spec.DefaultValue(autopilot))
                .Append(" · allowed ").Append(spec.Allowed).Append(")</i>\n")
                .Append("<i>").Append(spec.Description).Append("</i>\n");
        }

        var ingestion = ingestionOptions.Value;
        builder.Append("\n<b>Ingestion</b> <i>(appsettings-only)</i>: every ")
            .Append(ingestion.IntervalHours.ToString("F1", CultureInfo.InvariantCulture))
            .Append("h · max ").Append(ingestion.MaxItemsPerSource).Append("/source\n")
            .Append("\n/config set &lt;key&gt; &lt;value&gt; · /config reset &lt;key&gt; · models live in /models")
            .Append("\n<i>the right hand can change these too — always behind your ✅</i>");
        return builder.ToString();
    }

    private static string BuildHelp() =>
        """
        <b>Flow:</b> collect → analyze → signals → ideas → research

        <b>Run</b>
        /collect — fetch all sources now (or: <code>/collect hn</code>, 4chan, bluesky, lemmy, reddit)
        /analyze — AI triage of queued items
        /ideate 3 — AI sessions, rotating lenses · /ideate 3 nostalgia — force one
        /ideate from 12 45 — build ONE idea from exactly those signals (/signals to pick)
        /playbooks — the lens list (psych, absurd, nostalgia, copycat…)
        /drop your pitch here — YOUR idea: shaped → skeptic → web research
        /research 20 21 24 — web-validate one or several ideas
        /dig cycling — niche excavation: saturation map + spawned candidate ideas
        /audit — leaks check: unresearched ideas, failed jobs, unreviewed verdicts
        /sweep — verdict-improvement pass: old/under-researched ideas re-screened cheaply,
        worthy ones re-researched ON TOP of previous findings
        /kill 5 · /promote 5 — override any verdict with YOUR decision
        /queue — jobs: running/held/failed + cancel ✖️, resume ▶️, retry-all 🔁 buttons
        /cancel 7 — cancel a queued/held job (running ones finish)
        /verify 5 — mark reviewed (default /ideas hides verified) · /bump — +$5 today
        /note 5 your argument — next research must address it · /notes [5] — see your notes
        /find lego sorting — fuzzy idea search (🗒 in lists = has your notes)
        /appeal 5 — opus reviews the verdict
        /partner 5 — your right-hand's blunt take (worth your weekend or not)
        /origin 5 — your verbatim pitch + the idea at its best (no concern walls)
        /models — see/swap the model behind each stage (live-ping validated)
        /mine [text|list] — dig AI memory for pains; reply to a MINE card to go deeper
        just type (no /) — talk to your right hand: it reads ANYTHING in the db, proposes
        changes, and nothing applies until you tap ✅ on the audit card · /chat end resets
        tap-friendly: hints are emitted like /idea5 — both /idea 5 and /idea5 work
        /advise — AI reviews the pipeline itself

        <b>View</b>
        /best 8 — top signals, glance lines, idea links
        /ideas — browse with buttons (filters + pages) · /ideas top = best first
        /idea 5 — full trace card for idea number 5
        /signals — browse ALL signals: filters, 💎value/🕐 sort, 📅 day grouping, s-id taps
        /signal 123 — lineage card: where it came from, which ideas consumed it
        /config — runtime settings with allowed values · /config set sessions_per_day 4
        /top · /status · /costs

        <b>Scores</b>
        ⭐ N% = opportunity strength (100% ≈ unicorn) · evidence N% = research solidity
        ⭐ = graded by web research · ≈ = skeptic estimate (no research yet)
        Score = ingredients (demand/pay/build/gap). Status = the decision — one fatal
        flaw kills an idea regardless of a pretty score.

        <i>Numbers like 5 are idea ids — /ideas shows them as #5.</i>
        """;

    private async Task ReplyAsync(string html, CancellationToken cancellationToken)
    {
        // Long outputs (origin cards, big lists) split into reply-chained messages -
        // nothing may ever die of MESSAGE_TOO_LONG (the /origin 69 lesson).
        int? previousId = null;
        foreach (var chunk in IdeaEngine.Core.Common.MessageChunker.Split(html))
        {
            previousId = await SendReplyChunkAsync(chunk, previousId, cancellationToken) ?? previousId;
        }
    }

    private async Task<int?> SendReplyChunkAsync(string html, int? replyTo, CancellationToken cancellationToken)
    {
        var replyParameters = replyTo is { } id ? new Telegram.Bot.Types.ReplyParameters { MessageId = id } : null;
        try
        {
            var sent = await _bot!.SendMessage(
                chatId: _adminChatId,
                text: html,
                parseMode: ParseMode.Html,
                replyParameters: replyParameters,
                linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                cancellationToken: cancellationToken);
            return sent.MessageId;
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
        {
            // A formatting slip must never kill a command: retry as plain text.
            logger.LogWarning(ex, "HTML reply rejected; resending as plain text");
            var plain = System.Net.WebUtility.HtmlDecode(
                System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", string.Empty));
            var sent = await _bot!.SendMessage(
                chatId: _adminChatId,
                text: plain,
                replyParameters: replyParameters,
                linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                cancellationToken: cancellationToken);
            return sent.MessageId;
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
