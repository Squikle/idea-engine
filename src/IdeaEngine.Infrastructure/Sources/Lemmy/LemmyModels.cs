using System.Text.Json.Serialization;

namespace IdeaEngine.Infrastructure.Sources.Lemmy;

internal sealed record LemmyPostListResponse(
    [property: JsonPropertyName("posts")] IReadOnlyList<LemmyPostView>? Posts);

internal sealed record LemmyPostView(
    [property: JsonPropertyName("post")] LemmyPost? Post,
    [property: JsonPropertyName("creator")] LemmyActor? Creator,
    [property: JsonPropertyName("community")] LemmyActor? Community,
    [property: JsonPropertyName("counts")] LemmyCounts? Counts);

internal sealed record LemmyPost(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("published")] DateTimeOffset? Published);

internal sealed record LemmyActor(
    [property: JsonPropertyName("name")] string? Name);

internal sealed record LemmyCounts(
    [property: JsonPropertyName("score")] long? Score,
    [property: JsonPropertyName("comments")] int? Comments);

internal sealed record LemmyCommentListResponse(
    [property: JsonPropertyName("comments")] IReadOnlyList<LemmyCommentView>? Comments);

internal sealed record LemmyCommentView(
    [property: JsonPropertyName("comment")] LemmyComment? Comment,
    [property: JsonPropertyName("creator")] LemmyActor? Creator,
    [property: JsonPropertyName("counts")] LemmyCounts? Counts);

internal sealed record LemmyComment(
    [property: JsonPropertyName("content")] string? Content);
