using IdeaEngine.Infrastructure;
using IdeaEngine.Worker;
using Serilog;

// Bootstrap logger: catches configuration/startup failures before the real logger exists.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    DotEnv.Load();

    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Sink(TelegramLogSink.Instance, Serilog.Events.LogEventLevel.Warning));

    builder.Services.AddIdeaEngineInfrastructure(builder.Configuration);
    builder.Services.AddHostedService<StatusLifecycleService>(); // first: others report into it
    builder.Services.AddHostedService<StartupSummaryService>();
    builder.Services.AddHostedService<IngestionHostedService>();
    builder.Services.AddHostedService<TriageHostedService>();
    builder.Services.AddHostedService<TelegramCommandService>();
    builder.Services.AddHostedService<AutopilotHostedService>();
    builder.Services.AddHostedService<RetentionHostedService>();
    builder.Services.AddHostedService<JobRunnerHostedService>();

    // Last-resort exit hook: unhandled exceptions on non-host threads.
    AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        StatusLifecycleService.Current?
            .OfflineAsync($"fatal: {(eventArgs.ExceptionObject as Exception)?.GetType().Name ?? "unknown"}")
            .GetAwaiter().GetResult();

    var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    if (StatusLifecycleService.Current is { } statusBoard)
    {
        await statusBoard.OfflineAsync($"crashed: {ex.GetType().Name}");
    }

    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
