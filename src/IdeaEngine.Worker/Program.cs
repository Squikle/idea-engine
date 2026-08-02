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
        .Enrich.FromLogContext());

    builder.Services.AddIdeaEngineInfrastructure(builder.Configuration);
    builder.Services.AddHostedService<StartupSummaryService>();
    builder.Services.AddHostedService<IngestionHostedService>();

    var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
