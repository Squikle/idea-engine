using System.Runtime.CompilerServices;
using System.Xml.Linq;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Sources.ProductHunt;

/// <summary>Bound from configuration section <c>IdeaEngine:Sources:ProductHunt</c>.</summary>
public sealed class ProductHuntOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Keyless Atom feed of front-page launches. Token-based GraphQL is a later upgrade.</summary>
    public string FeedUrl { get; set; } = "https://www.producthunt.com/feed";
}

/// <summary>
/// Product Hunt launches = live competitor radar: what just got built, in whose niche.
/// Research and relations benefit even when no "pain" is extracted (trend context).
/// </summary>
public sealed class ProductHuntAdapter(
    HttpClient httpClient,
    TimeProvider timeProvider,
    IOptions<ProductHuntOptions> adapterOptions,
    ILogger<ProductHuntAdapter> logger) : ISourceAdapter
{
    public SourceKind Kind => SourceKind.ProductHunt;

    public async IAsyncEnumerable<RawItem> FetchAsync(
        SourceFetchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!adapterOptions.Value.Enabled)
        {
            yield break;
        }

        XDocument document;
        try
        {
            var xml = await httpClient.GetStringAsync(new Uri(adapterOptions.Value.FeedUrl), cancellationToken);
            document = XDocument.Parse(xml);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Product Hunt feed fetch failed");
            yield break;
        }

        XNamespace atom = "http://www.w3.org/2005/Atom";
        var yielded = 0;
        foreach (var entry in document.Descendants(atom + "entry"))
        {
            if (yielded >= options.MaxItems)
            {
                yield break;
            }

            var title = entry.Element(atom + "title")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var id = entry.Element(atom + "id")?.Value
                ?? entry.Element(atom + "link")?.Attribute("href")?.Value
                ?? title;
            var link = entry.Element(atom + "link")?.Attribute("href")?.Value;
            var contentHtml = entry.Element(atom + "content")?.Value ?? string.Empty;
            var body = System.Net.WebUtility.HtmlDecode(
                System.Text.RegularExpressions.Regex.Replace(contentHtml, "<[^>]+>", " "))
                .Trim();
            // Npgsql rejects non-UTC offsets for timestamptz; PH publishes -07:00.
            var published = DateTimeOffset.TryParse(
                entry.Element(atom + "published")?.Value, out var at)
                ? at.ToUniversalTime()
                : timeProvider.GetUtcNow();

            if (options.Since is { } since && published < since)
            {
                continue;
            }

            yielded++;
            yield return new RawItem
            {
                Source = SourceKind.ProductHunt,
                ExternalId = ContentHasher.Compute(id),
                Title = title,
                Body = body.Length > 0 ? TextClip.Clip(body, 900) : null,
                Url = link,
                Author = entry.Element(atom + "author")?.Element(atom + "name")?.Value,
                Community = "launches",
                CreatedAt = published,
                FetchedAt = timeProvider.GetUtcNow(),
            };
        }
    }
}
