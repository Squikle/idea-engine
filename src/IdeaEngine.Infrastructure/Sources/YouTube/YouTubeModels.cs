using System.Text.Json.Serialization;

namespace IdeaEngine.Infrastructure.Sources.YouTube;

internal sealed record YouTubeVideosResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<YouTubeVideo>? Items);

internal sealed record YouTubeVideo(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("snippet")] YouTubeSnippet? Snippet,
    [property: JsonPropertyName("statistics")] YouTubeStatistics? Statistics);

internal sealed record YouTubeSnippet(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("channelTitle")] string? ChannelTitle,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt);

internal sealed record YouTubeStatistics(
    [property: JsonPropertyName("viewCount")] string? ViewCount,
    [property: JsonPropertyName("commentCount")] string? CommentCount);

internal sealed record YouTubeSearchResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<YouTubeSearchItem>? Items);

internal sealed record YouTubeSearchItem(
    [property: JsonPropertyName("id")] YouTubeSearchId? Id,
    [property: JsonPropertyName("snippet")] YouTubeSnippet? Snippet);

internal sealed record YouTubeSearchId(
    [property: JsonPropertyName("videoId")] string? VideoId);

internal sealed record YouTubeCommentThreadsResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<YouTubeCommentThread>? Items);

internal sealed record YouTubeCommentThread(
    [property: JsonPropertyName("snippet")] YouTubeCommentThreadSnippet? Snippet);

internal sealed record YouTubeCommentThreadSnippet(
    [property: JsonPropertyName("topLevelComment")] YouTubeComment? TopLevelComment);

internal sealed record YouTubeComment(
    [property: JsonPropertyName("snippet")] YouTubeCommentSnippet? Snippet);

internal sealed record YouTubeCommentSnippet(
    [property: JsonPropertyName("textDisplay")] string? TextDisplay,
    [property: JsonPropertyName("authorDisplayName")] string? AuthorDisplayName,
    [property: JsonPropertyName("likeCount")] long? LikeCount);
