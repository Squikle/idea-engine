namespace IdeaEngine.Core.Sources;

/// <summary>A single comment attached to a <see cref="RawItem"/>.</summary>
public sealed record RawComment(string? Author, string Text, long Score);
