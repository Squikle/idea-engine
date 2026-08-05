using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Sources.StackExchange;

/// <summary>Bound from configuration section <c>IdeaEngine:Sources:StackExchange</c>.</summary>
public sealed class StackExchangeOptions
{
    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = "https://api.stackexchange.com/2.3";

    /// <summary>Sites to mine; each costs one request per cycle (keyless quota 300/day).</summary>
    public IList<string> Sites { get; } = ["stackoverflow", "superuser"];

    public int PageSize { get; set; } = 25;

    /// <summary>Ignore questions below this score - noise floor.</summary>
    public int MinScore { get; set; } = 3;
}

/// <summary>
/// High-vote UNANSWERED questions = pains the community itself failed to solve.
/// Pre-filtered by years of votes; triage turns them into signals.
/// </summary>
public sealed class StackExchangeAdapter(
    HttpClient httpClient,
    TimeProvider timeProvider,
    IOptions<StackExchangeOptions> adapterOptions,
    ILogger<StackExchangeAdapter> logger) : ISourceAdapter
{
    public SourceKind Kind => SourceKind.StackExchange;

    public async IAsyncEnumerable<RawItem> FetchAsync(
        SourceFetchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = adapterOptions.Value;
        if (!config.Enabled)
        {
            yield break;
        }

        var yielded = 0;
        foreach (var site in config.Sites)
        {
            Envelope? envelope;
            try
            {
                var url = $"{config.BaseUrl}/questions/unanswered?order=desc&sort=votes"
                    + $"&site={Uri.EscapeDataString(site)}&pagesize={config.PageSize}";
                envelope = await httpClient.GetFromJsonAsync<Envelope>(new Uri(url), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "StackExchange {Site} fetch failed", site);
                continue;
            }

            if (envelope?.ErrorId is { } errorId)
            {
                // SE errors come as 200-shaped bodies; swallowing them silently is banned.
                logger.LogWarning(
                    "StackExchange {Site} API error {ErrorId} {ErrorName}: {ErrorMessage}",
                    site, errorId, envelope.ErrorName, envelope.ErrorMessage);
                continue;
            }

            if (envelope?.QuotaRemaining is { } quota && quota < 20)
            {
                logger.LogWarning("StackExchange keyless quota low ({Quota}) — consider an API key", quota);
            }

            logger.LogInformation(
                "StackExchange {Site}: {Count} unanswered questions (quota {Quota})",
                site, envelope?.Items?.Count ?? 0, envelope?.QuotaRemaining);

            foreach (var question in envelope?.Items ?? [])
            {
                if (yielded >= options.MaxItems)
                {
                    yield break;
                }

                if (question.Title is not { Length: > 0 } || question.Score < config.MinScore)
                {
                    continue;
                }

                yielded++;
                yield return new RawItem
                {
                    Source = SourceKind.StackExchange,
                    ExternalId = $"{site}:{question.QuestionId.ToString(CultureInfo.InvariantCulture)}",
                    Title = System.Net.WebUtility.HtmlDecode(question.Title),
                    Body = $"unanswered for years despite {question.ViewCount} views · tags: "
                        + string.Join(", ", question.Tags ?? []),
                    Url = question.Link,
                    Community = question.Tags is { Count: > 0 } tags ? tags[0] : site,
                    Score = question.Score,
                    CommentCount = 0,
                    CreatedAt = DateTimeOffset.FromUnixTimeSeconds(question.CreationDate),
                    FetchedAt = timeProvider.GetUtcNow(),
                };
            }
        }
    }

    private sealed record Envelope(
        [property: JsonPropertyName("items")] IReadOnlyList<Question>? Items,
        [property: JsonPropertyName("quota_remaining")] int? QuotaRemaining,
        [property: JsonPropertyName("error_id")] int? ErrorId,
        [property: JsonPropertyName("error_name")] string? ErrorName,
        [property: JsonPropertyName("error_message")] string? ErrorMessage);

    private sealed record Question(
        [property: JsonPropertyName("question_id")] long QuestionId,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("link")] string? Link,
        [property: JsonPropertyName("score")] long Score,
        [property: JsonPropertyName("view_count")] long ViewCount,
        [property: JsonPropertyName("creation_date")] long CreationDate,
        [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags);
}
