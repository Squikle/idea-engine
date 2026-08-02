using System.Text.Json.Serialization;

namespace IdeaEngine.Infrastructure.Sources.Bluesky;

internal sealed record BlueskySessionResponse(
    [property: JsonPropertyName("accessJwt")] string? AccessJwt,
    [property: JsonPropertyName("handle")] string? Handle);

internal sealed record BlueskySearchResponse(
    [property: JsonPropertyName("posts")] IReadOnlyList<BlueskyPost>? Posts);

internal sealed record BlueskyPost(
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("author")] BlueskyAuthor? Author,
    [property: JsonPropertyName("record")] BlueskyRecord? Record,
    [property: JsonPropertyName("replyCount")] int? ReplyCount,
    [property: JsonPropertyName("repostCount")] int? RepostCount,
    [property: JsonPropertyName("likeCount")] int? LikeCount);

internal sealed record BlueskyAuthor(
    [property: JsonPropertyName("handle")] string? Handle,
    [property: JsonPropertyName("displayName")] string? DisplayName);

internal sealed record BlueskyRecord(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt);
