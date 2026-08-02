using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Sources.HackerNews;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IdeaEngine.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdeaEngineInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<IdeaEngineDbContext>(dbOptions => dbOptions
            .UseNpgsql(
                ConnectionStringBuilder.Resolve(configuration),
                npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention());

        AddSourceAdapters(services, configuration);

        return services;
    }

    private static void AddSourceAdapters(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HackerNewsOptions>(configuration.GetSection("IdeaEngine:Sources:HackerNews"));

        services
            .AddHttpClient<HackerNewsAdapter>(client =>
            {
                client.BaseAddress = new Uri("https://hn.algolia.com/api/v1/");
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddStandardResilienceHandler();

        services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<HackerNewsAdapter>());
    }
}
