using System.Text.Json.Serialization;

namespace IdeaEngine.Infrastructure.Sources.HackerNews;

internal sealed record AlgoliaSearchResponse(
    [property: JsonPropertyName("hits")] IReadOnlyList<AlgoliaHit>? Hits);

internal sealed record AlgoliaHit(
    [property: JsonPropertyName("objectID")] string? ObjectId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("points")] long? Points,
    [property: JsonPropertyName("num_comments")] int? NumComments,
    [property: JsonPropertyName("created_at_i")] long CreatedAtI,
    [property: JsonPropertyName("story_text")] string? StoryText,
    [property: JsonPropertyName("comment_text")] string? CommentText,
    [property: JsonPropertyName("_tags")] IReadOnlyList<string>? Tags);
