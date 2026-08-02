using System.Net;
using System.Reflection;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;

namespace IdeaEngine.Worker;

/// <summary>
/// Logs the startup banner, announces version changes with their patchnotes
/// (CHANGELOG.md section, embedded resource), then heartbeats quietly.
/// </summary>
internal sealed class StartupSummaryService(
    IHostEnvironment environment,
    IServiceScopeFactory scopeFactory,
    INotifier notifier,
    TimeProvider timeProvider,
    ILogger<StartupSummaryService> logger) : BackgroundService
{
    private const string LastVersionKey = "last_version";

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var version = typeof(StartupSummaryService).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        logger.LogInformation(
            "idea-engine {Version} started ({Environment})",
            version,
            environment.EnvironmentName);

        await AnnounceVersionChangeAsync(version, stoppingToken);

        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                logger.LogDebug("Heartbeat: worker alive");
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
    }

    private async Task AnnounceVersionChangeAsync(string version, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IdeaEngineDbContext>();

            var state = await db.AppState.FindAsync([LastVersionKey], cancellationToken);
            if (state?.Value == version)
            {
                return;
            }

            var section = ChangelogParser.TryGetSection(ReadEmbeddedChangelog(), version);
            var header = state is null
                ? $"<b>idea-engine {version}</b>"
                : $"<b>Updated {state.Value} → {version}</b>";
            var body = section is null
                ? string.Empty
                : "\n" + WebUtility.HtmlEncode(section.Length > 3200 ? section[..3200] + "…" : section);

            await notifier.SendAsync(header + body, cancellationToken);

            if (state is null)
            {
                db.AppState.Add(new AppStateEntity
                {
                    Key = LastVersionKey,
                    Value = version,
                    UpdatedAt = timeProvider.GetUtcNow(),
                });
            }
            else
            {
                state.Value = version;
                state.UpdatedAt = timeProvider.GetUtcNow();
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Version announcement failed (non-fatal)");
        }
    }

    private static string ReadEmbeddedChangelog()
    {
        var assembly = typeof(StartupSummaryService).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("CHANGELOG.md", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return string.Empty;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
