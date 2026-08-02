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
internal sealed class JobRunnerHostedService(
    IServiceScopeFactory scopeFactory,
    IServiceProvider serviceProvider,
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
            job.OriginMessageId,
            cancellationToken);
        await SaveProgressIdAsync(job.Id, progress.MessageId, cancellationToken);

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
                    await FailWithButtonsAsync(job, null, reason, cancellationToken);
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
            await FailWithButtonsAsync(job, ideaId, stopped, cancellationToken);
            return;
        }

        await progress.CompleteAsync(
            $"✅ done: {Core.Common.Ui.Verdict(result.Verdict)} · ⭐{result.Score * 100:F0}% · evidence {result.Confidence * 100:F0}%",
            CancellationToken.None);
        await SendReportWithButtonsAsync(result, cancellationToken);
        await MaybeAutoAppealAsync(result, cancellationToken);
    }

    private async Task ExecuteResearchAsync(JobEntity job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ResearchJobPayload>(job.PayloadJson, LlmJson.Options)
            ?? throw new InvalidOperationException("research payload unreadable");

        var progress = await progressNotifier.StartAsync(
            $"🔎 Research #{payload.IdeaId} (job #{job.Id}) · preparing…", job.OriginMessageId, cancellationToken);
        await SaveProgressIdAsync(job.Id, progress.MessageId, cancellationToken);

        var result = await researchCoordinator.RunAsync(payload.IdeaId, progress, wait: true, cancellationToken);
        if (result is null)
        {
            return;
        }

        if (result.StoppedReason is { } reason)
        {
            await progress.CompleteAsync($"⛔ stopped · {reason}", CancellationToken.None);
            await FailWithButtonsAsync(job, payload.IdeaId, reason, cancellationToken);
            return;
        }

        await progress.CompleteAsync(
            $"✅ done · {Core.Common.Ui.Verdict(result.Verdict)} · ⭐{result.Score * 100:F0}% · evidence {result.Confidence * 100:F0}%",
            CancellationToken.None);
        await SendReportWithButtonsAsync(result, cancellationToken);
        await MaybeAutoAppealAsync(result, cancellationToken);
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

            await notifier.SendAsync(
                $"⚖️ Auto-appeal for #{result.IdeaId}: killed despite ⭐{result.Score * 100:F0}% — second opinion running…",
                cancellationToken);
            var appeal = scope.ServiceProvider.GetRequiredService<IdeaEngine.Infrastructure.Research.AppealService>();
            var appealResult = await appeal.RunAsync(result.IdeaId, cancellationToken);
            await notifier.SendAsync(
                appealResult.StoppedReason is { } reason
                    ? $"⚖️ Auto-appeal #{result.IdeaId} ⛔ {reason}"
                    : appealResult.Html,
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
