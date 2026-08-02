using System.Text.Json.Serialization;

namespace IdeaEngine.Infrastructure.Sources.FourChan;

internal sealed record FourChanCatalogPage(
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("threads")] IReadOnlyList<FourChanPost>? Threads);

internal sealed record FourChanThreadResponse(
    [property: JsonPropertyName("posts")] IReadOnlyList<FourChanPost>? Posts);

/// <summary>Catalog entries and thread posts share the same shape on 4chan.</summary>
internal sealed record FourChanPost(
    [property: JsonPropertyName("no")] long No,
    [property: JsonPropertyName("time")] long Time,
    [property: JsonPropertyName("sub")] string? Subject,
    [property: JsonPropertyName("com")] string? CommentHtml,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("replies")] int? Replies,
    [property: JsonPropertyName("sticky")] int? Sticky,
    [property: JsonPropertyName("last_modified")] long? LastModified);
