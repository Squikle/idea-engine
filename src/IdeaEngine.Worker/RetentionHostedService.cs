using IdeaEngine.Infrastructure.Maintenance;

namespace IdeaEngine.Worker;

/// <summary>Runs retention/compliance once shortly after startup, then daily.</summary>
internal sealed class RetentionHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<RetentionHostedService> logger) : BackgroundService
{
    private static async Task MaybeWeeklyAuditAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        const string key = "last_audit";
        var db = services.GetRequiredService<IdeaEngine.Infrastructure.Persistence.IdeaEngineDbContext>();
        var state = await db.AppState.FindAsync([key], cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (state is not null
            && DateTimeOffset.TryParse(state.Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
            && now - last < TimeSpan.FromDays(6.5))
        {
            return;
        }

        var audit = services.GetRequiredService<IdeaEngine.Infrastructure.Maintenance.AuditService>();
        var notifier = services.GetRequiredService<IdeaEngine.Core.Notifications.INotifier>();
        var result = await audit.RunAsync(cancellationToken);
        await notifier.SendAsync(result.Html, cancellationToken);

        if (state is null)
        {
            db.AppState.Add(new IdeaEngine.Infrastructure.Persistence.Entities.AppStateEntity
            {
                Key = key,
                Value = now.ToString("O"),
                UpdatedAt = now,
            });
        }
        else
        {
            state.Value = now.ToString("O");
            state.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task MaybeMonthlySweepAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        const string key = "last_sweep";
        var db = services.GetRequiredService<IdeaEngine.Infrastructure.Persistence.IdeaEngineDbContext>();
        var state = await db.AppState.FindAsync([key], cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (state is not null
            && DateTimeOffset.TryParse(state.Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
            && now - last < TimeSpan.FromDays(30))
        {
            return;
        }

        var reeval = services.GetRequiredService<IdeaEngine.Infrastructure.Maintenance.ReevalService>();
        var notifier = services.GetRequiredService<IdeaEngine.Core.Notifications.INotifier>();
        var result = await reeval.RunAsync(null, cancellationToken);
        if (result.StoppedReason is null)
        {
            await notifier.SendAsync("🗓 monthly auto-sweep:\n" + result.Html, cancellationToken);
        }

        if (state is null)
        {
            db.AppState.Add(new IdeaEngine.Infrastructure.Persistence.Entities.AppStateEntity
            {
                Key = key,
                Value = now.ToString("O"),
                UpdatedAt = now,
            });
        }
        else
        {
            state.Value = now.ToString("O");
            state.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        await RunSafeAsync(stoppingToken);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunSafeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
    }

    private async Task RunSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(cancellationToken);
            await MaybeWeeklyAuditAsync(scope.ServiceProvider, cancellationToken);
            await MaybeMonthlySweepAsync(scope.ServiceProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Retention run failed; next attempt in 24h");
        }
    }
}
