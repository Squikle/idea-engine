using IdeaEngine.Core.Common;
using Microsoft.Extensions.Logging;

namespace IdeaEngine.Infrastructure.Research;

/// <summary>
/// Fetches result pages and reduces them to analyzable text. Snippets prove existence;
/// pages answer pricing/feature questions. HTML only, size-capped, never throws.
/// </summary>
public sealed class PageFetcher(
    HttpClient httpClient,
    ILogger<PageFetcher> logger)
{
    public async Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken cancellationToken)
    {
        // With ResponseHeadersRead, HttpClient.Timeout only bounds the HEADERS; a tarpit
        // server dripping body bytes hangs ReadAsStringAsync forever (the job-#37 incident).
        // This linked 15s box bounds the whole fetch.
        using var timebox = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timebox.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await httpClient.GetAsync(
                new Uri(url), HttpCompletionOption.ResponseHeadersRead, timebox.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                && !contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                return null; // PDFs, images, binaries - not worth the tokens
            }

            var html = await response.Content.ReadAsStringAsync(timebox.Token);
            if (html.Length > 400_000)
            {
                html = html[..400_000];
            }

            var text = HtmlText.ToPlainText(html);
            return text.Length <= maxChars ? text : text[..maxChars];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Page fetch timeboxed out (15s): {Url}", url);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Page fetch failed: {Url}", url);
            return null;
        }
    }
}
