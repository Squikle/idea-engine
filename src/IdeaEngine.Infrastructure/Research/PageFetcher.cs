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
        try
        {
            using var response = await httpClient.GetAsync(
                new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (html.Length > 400_000)
            {
                html = html[..400_000];
            }

            var text = HtmlText.ToPlainText(html);
            return text.Length <= maxChars ? text : text[..maxChars];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Page fetch failed: {Url}", url);
            return null;
        }
    }
}
