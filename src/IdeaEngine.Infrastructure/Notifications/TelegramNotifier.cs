using IdeaEngine.Core.Notifications;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>Sends owner notifications to the admin chat. Failures are logged, never thrown.</summary>
public sealed class TelegramNotifier(
    ITelegramBotClient botClient,
    long adminChatId,
    ILogger<TelegramNotifier> logger) : INotifier
{
    private const int ChunkLimit = 4000; // Telegram hard limit is 4096

    public async Task SendAsync(string html, CancellationToken cancellationToken)
    {
        foreach (var chunk in SplitAtLines(html))
        {
            await SendChunkAsync(chunk, cancellationToken);
        }
    }

    private async Task SendChunkAsync(string html, CancellationToken cancellationToken)
    {
        try
        {
            await botClient.SendMessage(
                chatId: adminChatId,
                text: html,
                parseMode: ParseMode.Html,
                linkPreviewOptions: LinkPreviewOptions.Disabled, // keeps messages compact
                cancellationToken: cancellationToken);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
        {
            // Formatting slip: retry stripped so content still arrives.
            logger.LogWarning(ex, "HTML message rejected; resending as plain text");
            try
            {
                var plain = System.Net.WebUtility.HtmlDecode(
                    System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", string.Empty));
                await botClient.SendMessage(
                    chatId: adminChatId,
                    text: plain,
                    linkPreviewOptions: LinkPreviewOptions.Disabled,
                    cancellationToken: cancellationToken);
            }
            catch (Exception inner) when (inner is not OperationCanceledException)
            {
                logger.LogError(inner, "Telegram send failed; message dropped ({Length} chars)", html.Length);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Telegram send failed; message dropped ({Length} chars)", html.Length);
        }
    }

    /// <summary>Splits oversized messages at line boundaries so nothing is ever dropped.</summary>
    private static IEnumerable<string> SplitAtLines(string html)
    {
        if (html.Length <= ChunkLimit)
        {
            yield return html;
            yield break;
        }

        var current = new System.Text.StringBuilder();
        foreach (var line in html.Split('\n'))
        {
            if (current.Length + line.Length + 1 > ChunkLimit && current.Length > 0)
            {
                yield return current.ToString().TrimEnd();
                current.Clear();
            }

            current.Append(line).Append('\n');
        }

        if (current.Length > 0)
        {
            yield return current.ToString().TrimEnd();
        }
    }
}
