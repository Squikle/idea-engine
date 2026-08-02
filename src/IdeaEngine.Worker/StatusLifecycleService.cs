using IdeaEngine.Core.Notifications;

namespace IdeaEngine.Worker;

/// <summary>
/// Owns the status board lifecycle: creates+pins on start, flips to OFFLINE on any exit.
/// Registered first so the board exists before other services report into it.
/// </summary>
internal sealed class StatusLifecycleService(
    IStatusBoard statusBoard,
    IHostApplicationLifetime lifetime) : IHostedService
{
    /// <summary>Emergency handle for crash paths outside DI (Program catch, AppDomain hook).</summary>
    internal static IStatusBoard? Current { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Current = statusBoard;
        await statusBoard.InitializeAsync(cancellationToken);

        // Graceful shutdown (SIGINT/SIGTERM/host stop). OfflineAsync is self-timeboxed to 5s.
        lifetime.ApplicationStopping.Register(() =>
            statusBoard.OfflineAsync("shutdown").GetAwaiter().GetResult());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
