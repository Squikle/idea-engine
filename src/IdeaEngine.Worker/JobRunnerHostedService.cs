using System.Text.Json;
using Telegram.Bot;
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
/// <summary>Bound from configuration section <c>IdeaEngine:Jobs</c>.</summary>
internal sealed class JobRunnerOptions
{
    public int DropTimeoutMinutes { get; set; } = 18;

    public int ResearchTimeoutMinutes { get; set; } = 12;

    public int DigTimeoutMinutes { get; set; } = 10;

    /// <summary>Appeal/partner run on the LIGHT lane - single calls, short leash.</summary>
    public int LightTimeoutMinutes { get; set; } = 6;
}

internal sealed class JobRunnerHostedService(
    IServiceScopeFactory scopeFactory,
    IServiceProvider serviceProvider,
    ResearchCoordinator researchCoordinator,
    IProgressNotifier progressNotifier,
    IStatusTracker statusTracker,
    INotifier notifier,
    Microsoft.Extensions.Options.IOptions<JobRunnerOptions> runnerOptions,
    ILogger<JobRunnerHostedService> logger) : BackgroundService
{
    private const int MaxAttempts = 2;

    /// <summary>One coalesced budget card per day - not one per starved job.</summary>
    private DateOnly _capCardShownOn;

    /// <summary>Same coalescing for OpenRouter credit exhaustion (different remedy).</summary>
    private DateOnly _creditCardShownOn;

    /// <summary>HEAVY = long multi-call pipelines (serial). LIGHT = single-call reviews
    /// that must never wait behind a 12-minute research. Lanes run in parallel.</summary>
    private static readonly string[] HeavyKinds = ["drop", "research", "dig"];

    private static readonly string[] LightKinds = ["appeal", "partner"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        await RecoverAsync(stoppingToken);
        await Task.WhenAll(
            RunLaneAsync(HeavyKinds, stoppingToken),
            RunLaneAsync(LightKinds, stoppingToken));
    }

    private async Task RunLaneAsync(string[] kinds, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            JobEntity? job = null;
            try
            {
                using (var scope = scopeFactory.CreateScope())
                {
                    job = await scope.ServiceProvider.GetRequiredService<JobService>()
                        .ClaimNextAsync(kinds, stoppingToken);
                }

                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
                    continue;
                }

                var timeout = TimeSpan.FromMinutes(job.Kind switch
                {
                    "drop" => runnerOptions.Value.DropTimeoutMinutes,
                    "dig" => runnerOptions.Value.DigTimeoutMinutes,
                    "appeal" or "partner" => runnerOptions.Value.LightTimeoutMinutes,
                    _ => runnerOptions.Value.ResearchTimeoutMinutes,
                });

                using var manualCts = new CancellationTokenSource();
                using var watchdog = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken, manualCts.Token, watchdog.Token);
                RunningJobs.Register(job.Id, manualCts);
                try
                {
                    await ExecuteJobAsync(job, linked.Token);
                    await FinishAsync(job.Id, error: null, stoppingToken);
                }
                catch (OperationCanceledException) when (manualCts.IsCancellationRequested)
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<JobService>()
                        .MarkCanceledAsync(job.Id, CancellationToken.None);
                    await notifier.SendAsync(
                        $"🛑 Job <b>#{job.Id}</b> ({job.Kind}) canceled mid-run — tokens already spent are lost.",
                        CancellationToken.None);
                }
                catch (OperationCanceledException) when (watchdog.IsCancellationRequested
                    && !stoppingToken.IsCancellationRequested)
                {
                    // The whole point of the watchdog: a stalled await can no longer freeze the queue.
                    await FinishAsync(job.Id, error: $"watchdog timeout after {timeout.TotalMinutes:F0}m", stoppingToken);
                    await SendTimeoutCardAsync(job, timeout, stoppingToken);
                }
                finally
                {
                    RunningJobs.Unregister(job.Id);
                }
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
            case "dig":
                await ExecuteDigAsync(job, cancellationToken);
                break;
            case "appeal":
                await ExecuteAppealAsync(job, cancellationToken);
                break;
            case "partner":
                await ExecutePartnerAsync(job, cancellationToken);
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
            job.OriginMessageId,
            cancellationToken);
        await SaveProgressIdAsync(job.Id, progress.MessageId, cancellationToken);

        var ideaId = payload.IdeaId;
        var advancedDrop = true;
        if (ideaId is { } resumedId)
        {
            // Resumed checkpoint: the payload predates the verdict - ask the DB.
            using var resumeScope = scopeFactory.CreateScope();
            var resumeDb = resumeScope.ServiceProvider
                .GetRequiredService<IdeaEngine.Infrastructure.Persistence.IdeaEngineDbContext>();
            advancedDrop = (await resumeDb.Ideas.FindAsync([resumedId], cancellationToken))?.Status != "dismissed";
        }

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
                    await FailWithButtonsAsync(job, null, reason, cancellationToken);
                    return;
                }

                ideaId = shaped.IdeaId;
                advancedDrop = shaped.Advanced;
                if (shaped.Title is { Length: > 0 } shapedTitle)
                {
                    await progress.SetHeaderAsync(
                        $"📦 <b>Drop #{ideaId} · {System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(shapedTitle, 60))}</b>",
                        cancellationToken);
                }

                await notifier.SendAsync(shaped.Html, cancellationToken);

                // Semantic memory: link duplicates/variants so re-drops enrich, not fragment.
                if (ideaId is { } newId)
                {
                    var relations = await scope.ServiceProvider
                        .GetRequiredService<IdeaEngine.Infrastructure.Ideation.RelationService>()
                        .LinkAsync(newId, cancellationToken);
                    if (relations.Relations.Count > 0)
                    {
                        await notifier.SendAsync(
                            "🧬 <b>Related ideas found</b>\n" + string.Join('\n', relations.Relations.Select(r =>
                                $"• #{r.Id} <i>({r.Kind})</i> {System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(r.Title, 60))} — /idea {r.Id}")) +
                            "\n<i>linked on both sides; verdicts of relatives are context, not law</i>",
                            cancellationToken);
                    }
                }
            }
            finally
            {
                OperationGates.Ideation.Release();
            }

            if (ideaId is null)
            {
                return;
            }

            if (!advancedDrop)
            {
                // Operator drops earn an automatic day in court: the skeptic is strict by
                // design, and a cheap kill of a human-typed pitch deserves a second look.
                await progress.UpdateAsync(
                    $"☠️ skeptic killed #{ideaId} — court of appeal reviewing…", cancellationToken);
                AppealResult appealResult;
                using (var appealScope = scopeFactory.CreateScope())
                {
                    appealResult = await appealScope.ServiceProvider
                        .GetRequiredService<IdeaEngine.Infrastructure.Research.AppealService>()
                        .RunAsync(ideaId.Value, cancellationToken);
                }

                if (appealResult.StoppedReason is { } appealSkip)
                {
                    await progress.CompleteAsync(
                        $"☠️ skeptic killed #{ideaId} · appeal skipped — {appealSkip} · " +
                        $"{IdeaEngine.Core.Common.Ui.Cmd("appeal", ideaId.Value)} to retry · {IdeaEngine.Core.Common.Ui.Cmd("research", ideaId.Value)} to override",
                        CancellationToken.None);
                    return;
                }

                if (appealResult.Html is { Length: > 0 })
                {
                    await notifier.SendAsync(appealResult.Html, cancellationToken);
                }

                if (!appealResult.Overturned)
                {
                    await progress.CompleteAsync(
                        $"☠️ #{ideaId} killed by skeptic, appeal upheld — " +
                        $"{IdeaEngine.Core.Common.Ui.Cmd("research", ideaId.Value)} to override · {IdeaEngine.Core.Common.Ui.Cmd("note", ideaId.Value)} to argue first",
                        CancellationToken.None);
                    return;
                }

                await progress.UpdateAsync(
                    "⚖️ appeal overturned the kill — research proceeds…", cancellationToken);
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
            await HandleStopAsync(job, ideaId, stopped, progress, cancellationToken);
            return;
        }

        await progress.CompleteAsync(
            $"✅ done: {Core.Common.Ui.Verdict(result.Verdict)} · ⭐{result.Score * 100:F0}% · evidence {result.Confidence * 100:F0}%",
            CancellationToken.None);
        await SendReportWithButtonsAsync(result, cancellationToken);
        await MaybeAutoAppealAsync(result, cancellationToken);
    }

    private async Task ExecuteDigAsync(JobEntity job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<DigJobPayload>(job.PayloadJson, LlmJson.Options)
            ?? throw new InvalidOperationException("dig payload unreadable");

        var progress = await progressNotifier.StartAsync(
            $"⛏ Dig (job #{job.Id}) · “{IdeaEngine.Core.Common.TextClip.Clip(payload.Topic, 40)}”…",
            job.OriginMessageId, cancellationToken);
        await SaveProgressIdAsync(job.Id, progress.MessageId, cancellationToken);

        await statusTracker.BeginAsync(
            Tracks.Dig, IdeaEngine.Core.Common.TextClip.Clip(payload.Topic, 30), cancellationToken);
        IdeaEngine.Infrastructure.Research.DigRunResult result;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dig = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Research.DigService>();
            result = await dig.RunAsync(payload.Topic, progress, cancellationToken);
        }
        finally
        {
            await statusTracker.EndAsync(Tracks.Dig, null, CancellationToken.None);
        }

        await statusTracker.EndAsync(
            Tracks.Dig,
            result.StoppedReason is null ? $"“{IdeaEngine.Core.Common.TextClip.Clip(payload.Topic, 20)}” +{result.SpawnedIdeas}🌱" : "⛔ stopped",
            CancellationToken.None);

        if (result.StoppedReason is { } reason)
        {
            await HandleStopAsync(job, null, reason, progress, cancellationToken);
            return;
        }

        await progress.CompleteAsync(
            $"⛏ ✅ done · {result.SpawnedIdeas} idea(s) spawned · map below", CancellationToken.None);
        await SendWithResearchAllAsync(result.Html, result.SpawnedIds, cancellationToken);
    }

    /// <summary>Send html with a one-tap "research all" button when there are spawned ids.</summary>
    private async Task SendWithResearchAllAsync(
        string html, IReadOnlyList<long> ideaIds, CancellationToken cancellationToken)
    {
        var bot = serviceProvider.GetService<Telegram.Bot.ITelegramBotClient>();
        var telegram = serviceProvider.GetService<IdeaEngine.Infrastructure.Notifications.TelegramOptions>();
        if (bot is null || telegram is not { IsConfigured: true } || ideaIds.Count == 0)
        {
            await notifier.SendAsync(html, cancellationToken);
            return;
        }

        try
        {
            await bot.SendMessage(
                chatId: telegram.AdminChatId!.Value,
                text: html,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                replyMarkup: new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
                [
                    [Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData(
                        $"🔎 Research all {ideaIds.Count}", $"rall|{string.Join(',', ideaIds)}")],
                ]),
                linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "research-all keyboard send failed; plain fallback");
            await notifier.SendAsync(html, cancellationToken);
        }
    }

    private async Task ExecuteResearchAsync(JobEntity job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ResearchJobPayload>(job.PayloadJson, LlmJson.Options)
            ?? throw new InvalidOperationException("research payload unreadable");

        string? ideaTitle;
        using (var titleScope = scopeFactory.CreateScope())
        {
            var db = titleScope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Persistence.IdeaEngineDbContext>();
            ideaTitle = (await db.Ideas.FindAsync([payload.IdeaId], cancellationToken))?.Title;
        }

        var progress = await progressNotifier.StartAsync(
            ideaTitle is { Length: > 0 }
                ? $"🔎 <b>Research #{payload.IdeaId} · {System.Net.WebUtility.HtmlEncode(IdeaEngine.Core.Common.TextClip.Clip(ideaTitle, 60))}</b>"
                : $"🔎 Research #{payload.IdeaId} (job #{job.Id})",
            job.OriginMessageId, cancellationToken);
        await SaveProgressIdAsync(job.Id, progress.MessageId, cancellationToken);

        var result = await researchCoordinator.RunAsync(payload.IdeaId, progress, wait: true, cancellationToken);
        if (result is null)
        {
            return;
        }

        if (result.StoppedReason is { } reason)
        {
            await HandleStopAsync(job, payload.IdeaId, reason, progress, cancellationToken);
            return;
        }

        await progress.CompleteAsync(
            $"✅ done · {Core.Common.Ui.Verdict(result.Verdict)} · ⭐{result.Score * 100:F0}% · evidence {result.Confidence * 100:F0}%",
            CancellationToken.None);
        await SendReportWithButtonsAsync(result, cancellationToken);
        await MaybeAutoAppealAsync(result, cancellationToken);
    }

    private async Task ExecuteAppealAsync(JobEntity job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<AppealJobPayload>(job.PayloadJson, LlmJson.Options)
            ?? throw new InvalidOperationException("appeal payload unreadable");
        var progress = await progressNotifier.StartAsync(
            $"⚖️ Appeal (job #{job.Id}) · idea #{payload.IdeaId} under review…",
            job.OriginMessageId,
            cancellationToken);
        await SaveProgressIdAsync(job.Id, progress.MessageId, cancellationToken);

        using var scope = scopeFactory.CreateScope();
        var appeal = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Research.AppealService>();
        var result = await appeal.RunAsync(payload.IdeaId, cancellationToken);
        if (result.StoppedReason is { } reason)
        {
            await HandleStopAsync(job, payload.IdeaId, reason, progress, cancellationToken);
            return;
        }

        await progress.CompleteAsync(
            result.NewStatus is { } status
                ? $"⚖️ Appeal #{payload.IdeaId} · OVERTURNED → {status}"
                : $"⚖️ Appeal #{payload.IdeaId} · verdict upheld",
            CancellationToken.None);
        await notifier.SendAsync(result.Html, cancellationToken);
    }

    private async Task ExecutePartnerAsync(JobEntity job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<PartnerJobPayload>(job.PayloadJson, LlmJson.Options)
            ?? throw new InvalidOperationException("partner payload unreadable");
        var progress = await progressNotifier.StartAsync(
            $"🤝 Partner (job #{job.Id}) · reading #{payload.IdeaId}'s full trail…",
            job.OriginMessageId,
            cancellationToken);
        await SaveProgressIdAsync(job.Id, progress.MessageId, cancellationToken);

        using var scope = scopeFactory.CreateScope();
        var partner = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Research.PartnerService>();
        var result = await partner.RunAsync(payload.IdeaId, cancellationToken);
        if (result.StoppedReason is { } reason)
        {
            await HandleStopAsync(job, payload.IdeaId, reason, progress, cancellationToken);
            return;
        }

        await progress.CompleteAsync($"🤝 Partner take on #{payload.IdeaId} ↓", CancellationToken.None);
        await notifier.SendAsync(result.Html, cancellationToken);
    }

    private async Task MaybeAutoAppealAsync(
        IdeaEngine.Infrastructure.Research.ResearchRunResult result, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var options = scope.ServiceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<IdeaEngine.Infrastructure.Research.AppealOptions>>().Value;
            if (!options.Enabled || result.Verdict != "no-go" || result.Score < options.AutoAppealMinScore)
            {
                return;
            }

            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
            var (appealJobId, _) = await jobs.EnqueueAsync(
                "appeal", new AppealJobPayload(result.IdeaId), null, cancellationToken);
            await notifier.SendAsync(
                $"⚖️ Auto-appeal queued (light lane, job #{appealJobId}) for #{result.IdeaId}: " +
                $"killed despite ⭐{result.Score * 100:F0}%.",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Auto-appeal failed for idea {IdeaId}", result.IdeaId);
        }
    }

    private async Task SaveProgressIdAsync(long jobId, int? messageId, CancellationToken cancellationToken)
    {
        if (messageId is null)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<JobService>()
            .SetProgressMessageAsync(jobId, messageId, cancellationToken);
    }

    private async Task SendTimeoutCardAsync(JobEntity job, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var bot = serviceProvider.GetService<Telegram.Bot.ITelegramBotClient>();
        var telegram = serviceProvider.GetService<IdeaEngine.Infrastructure.Notifications.TelegramOptions>();
        if (bot is null || telegram is not { IsConfigured: true })
        {
            return;
        }

        try
        {
            await bot.SendMessage(
                chatId: telegram.AdminChatId!.Value,
                text: $"⏱ <b>Job #{job.Id}</b> ({job.Kind}) hit the {timeout.TotalMinutes:F0}-minute watchdog " +
                    "and was stopped so the queue keeps moving. Usually a stalled network call — retry tends to work.",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                replyParameters: job.OriginMessageId is { } origin
                    ? new Telegram.Bot.Types.ReplyParameters { MessageId = origin }
                    : null,
                replyMarkup: new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
                [
                    [Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData(
                        $"🔁 Retry job #{job.Id}", $"job|retry|{job.Id}")],
                ]),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Timeout card send failed");
        }
    }

    /// <summary>Report message with one-tap decision buttons; falls back to plain notifier.</summary>
    private async Task SendReportWithButtonsAsync(
        IdeaEngine.Infrastructure.Research.ResearchRunResult result, CancellationToken cancellationToken)
    {
        var bot = serviceProvider.GetService<Telegram.Bot.ITelegramBotClient>();
        var telegram = serviceProvider.GetService<IdeaEngine.Infrastructure.Notifications.TelegramOptions>();
        if (bot is null || telegram is not { IsConfigured: true } || result.IdeaId == 0)
        {
            await notifier.SendAsync(result.Html, cancellationToken);
            return;
        }

        var id = result.IdeaId;
        var keyboard = new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
        [
            [
                Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("✅ Verified", $"verify|{id}"),
                Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("🔁 Re-research", $"rr|{id}"),
            ],
            [
                Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("🔥 Promote", $"promoteb|{id}"),
                Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("☠️ Kill", $"killb|{id}"),
            ],
        ]);

        try
        {
            await bot.SendMessage(
                chatId: telegram.AdminChatId!.Value,
                text: result.Html,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                replyMarkup: keyboard,
                linkPreviewOptions: Telegram.Bot.Types.LinkPreviewOptions.Disabled,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Report-with-buttons failed; plain fallback");
            await notifier.SendAsync(result.Html, cancellationToken);
        }
    }

    /// <summary>
    /// Routes a stop: budget-cap stops PARK the job (auto-resumes at UTC-midnight cap reset,
    /// or instantly on bump) with one coalesced card per day; real failures stay failures.
    /// </summary>
    private async Task HandleStopAsync(
        JobEntity job, long? ideaId, string reason, IProgressHandle progress, CancellationToken cancellationToken)
    {
        // Provider wallet empty (HTTP 402) is NOT our cap - bumping can't help, only a top-up.
        var isCredit = reason.Contains("402", StringComparison.Ordinal)
            || reason.Contains("credit", StringComparison.OrdinalIgnoreCase);
        if (isCredit)
        {
            var retryAt = DateTimeOffset.UtcNow.AddHours(4); // probe again even without a top-up
            using (var scope = scopeFactory.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<JobService>()
                    .HoldAsync(job.Id, retryAt, reason, cancellationToken);
            }

            await progress.CompleteAsync(
                "⏸ held · OpenRouter credit balance is empty — top up, then tap resume",
                CancellationToken.None);

            var creditToday = DateOnly.FromDateTime(DateTime.UtcNow);
            if (_creditCardShownOn != creditToday)
            {
                _creditCardShownOn = creditToday;
                var creditBot = serviceProvider.GetService<Telegram.Bot.ITelegramBotClient>();
                var creditTelegram = serviceProvider.GetService<IdeaEngine.Infrastructure.Notifications.TelegramOptions>();
                if (creditBot is not null && creditTelegram is { IsConfigured: true })
                {
                    try
                    {
                        await creditBot.SendMessage(
                            chatId: creditTelegram.AdminChatId!.Value,
                            text: "🏦 <b>OpenRouter prepaid balance is EMPTY</b> — every model call returns 402.\n" +
                                "This is not our daily cap: bumping won't help and there is no automatic reset.\n" +
                                "Fix: openrouter.ai → <b>Credits</b> → add funds (consider auto-top-up). " +
                                "Jobs are held; after topping up tap resume:",
                            parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                            replyMarkup: new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
                            [
                                [Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData(
                                    "▶️ topped up — resume all", "job|releaseheld|0")],
                            ]),
                            cancellationToken: cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Credit card send failed");
                    }
                }
            }

            return;
        }

        var isBudget = reason.Contains("cap", StringComparison.OrdinalIgnoreCase);
        if (!isBudget)
        {
            await progress.CompleteAsync($"⛔ stopped · {reason}", CancellationToken.None);
            await FailWithButtonsAsync(job, ideaId, reason, cancellationToken);
            return;
        }

        var resetAt = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1), TimeSpan.Zero);
        using (var scope = scopeFactory.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobService>()
                .HoldAsync(job.Id, resetAt, reason, cancellationToken);
        }

        await progress.CompleteAsync(
            $"⏸ held · budget cap · auto-resumes at cap reset (or 💸 bump to resume now)",
            CancellationToken.None);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_capCardShownOn == today)
        {
            return; // the day's card already offers resume-all
        }

        _capCardShownOn = today;
        var bot = serviceProvider.GetService<Telegram.Bot.ITelegramBotClient>();
        var telegram = serviceProvider.GetService<IdeaEngine.Infrastructure.Notifications.TelegramOptions>();
        if (bot is null || telegram is not { IsConfigured: true })
        {
            return;
        }

        try
        {
            await bot.SendMessage(
                chatId: telegram.AdminChatId!.Value,
                text: $"⏸ <b>Budget cap reached</b> — jobs are being HELD, not failed.\n" +
                    $"<i>{System.Net.WebUtility.HtmlEncode(reason)}</i>\n" +
                    "They auto-resume at cap reset (00:00 UTC ≈ 20:00 Ontario). Or resume now:",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                replyMarkup: new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
                [
                    [
                        Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData(
                            "💸 +$5 & resume all", "budget|bump|0"),
                        Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData(
                            "▶️ resume without bump", "job|releaseheld|0"),
                    ],
                ]),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cap card send failed");
        }
    }

    /// <summary>Marks the job failed and posts an actionable card: retry, and +$5 when it was a budget cap.</summary>
    private async Task FailWithButtonsAsync(
        JobEntity job, long? ideaId, string reason, CancellationToken cancellationToken)
    {
        await FinishAsync(job.Id, error: reason, cancellationToken);

        var bot = serviceProvider.GetService<Telegram.Bot.ITelegramBotClient>();
        var telegram = serviceProvider.GetService<IdeaEngine.Infrastructure.Notifications.TelegramOptions>();
        if (bot is null || telegram is not { IsConfigured: true })
        {
            return;
        }

        var buttons = new List<Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton>
        {
            Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData(
                $"🔁 Retry job #{job.Id}", $"job|retry|{job.Id}"),
        };
        if (reason.Contains("cap", StringComparison.OrdinalIgnoreCase))
        {
            buttons.Add(Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData(
                "💸 +$5 today", $"budget|bump|{job.Id}"));
        }

        var ideaPart = ideaId is { } id ? $" · idea #{id} (/idea {id})" : string.Empty;
        try
        {
            await bot.SendMessage(
                chatId: telegram.AdminChatId!.Value,
                text: $"⛔ <b>Job #{job.Id} stopped</b>{ideaPart}\n{System.Net.WebUtility.HtmlEncode(reason)}",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                replyParameters: job.OriginMessageId is { } origin
                    ? new Telegram.Bot.Types.ReplyParameters { MessageId = origin }
                    : null,
                replyMarkup: new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failure card send failed");
        }
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
