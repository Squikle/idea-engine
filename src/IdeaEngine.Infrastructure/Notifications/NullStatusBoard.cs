using IdeaEngine.Core.Notifications;

namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>Stands in when Telegram is not configured.</summary>
public sealed class NullStatusBoard : IStatusBoard
{
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpdateAsync(
        string activity, string? detail, DateTimeOffset? nextCycleAt, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task OfflineAsync(string reason) => Task.CompletedTask;
}
