using IdeaEngine.Core.Notifications;
using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Ingestion;
using IdeaEngine.Infrastructure.Notifications;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Sources.HackerNews;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

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
        AddTelegram(services, configuration);

        services.Configure<IngestionOptions>(configuration.GetSection("IdeaEngine:Ingestion"));
        services.AddScoped<IngestionService>();

        return services;
    }

    private static void AddTelegram(IServiceCollection services, IConfiguration configuration)
    {
        var telegram = new TelegramOptions
        {
            BotToken = configuration["TELEGRAM_BOT_TOKEN"],
            AdminChatId = long.TryParse(configuration["TELEGRAM_ADMIN_CHAT_ID"], out var chatId)
                ? chatId
                : null,
        };

        if (!telegram.IsConfigured)
        {
            services.AddSingleton<INotifier, NullNotifier>();
            return;
        }

        services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(telegram.BotToken!));
        services.AddSingleton<INotifier>(sp => new TelegramNotifier(
            sp.GetRequiredService<ITelegramBotClient>(),
            telegram.AdminChatId!.Value,
            sp.GetRequiredService<ILogger<TelegramNotifier>>()));
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
