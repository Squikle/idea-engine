namespace IdeaEngine.Infrastructure.Persistence.Entities;

/// <summary>Tiny key-value store for worker state (last announced version, etc.).</summary>
public sealed class AppStateEntity
{
    public required string Key { get; set; }

    public required string Value { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
