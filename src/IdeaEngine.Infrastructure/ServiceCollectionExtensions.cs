using IdeaEngine.Core.Notifications;
using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Ingestion;
using IdeaEngine.Infrastructure.Notifications;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Sources.Bluesky;
using IdeaEngine.Infrastructure.Sources.FourChan;
using IdeaEngine.Infrastructure.Sources.HackerNews;
using IdeaEngine.Infrastructure.Sources.Lemmy;
using IdeaEngine.Infrastructure.Sources.RedditRss;
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
        services.AddSingleton<IngestionCoordinator>();

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

        services.AddSingleton(telegram); // consumed by the Worker command listener

        if (!telegram.IsConfigured)
        {
            services.AddSingleton<INotifier, NullNotifier>();
            services.AddSingleton<IStatusBoard, NullStatusBoard>();
            return;
        }

        services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(telegram.BotToken!));
        services.AddSingleton<INotifier>(sp => new TelegramNotifier(
            sp.GetRequiredService<ITelegramBotClient>(),
            telegram.AdminChatId!.Value,
            sp.GetRequiredService<ILogger<TelegramNotifier>>()));
        services.AddSingleton<IStatusBoard>(sp => new TelegramStatusBoard(
            sp.GetRequiredService<ITelegramBotClient>(),
            telegram.AdminChatId!.Value,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<TelegramStatusBoard>>()));
    }

    private static void AddSourceAdapters(IServiceCollection services, IConfiguration configuration)
    {
        var userAgent = BuildUserAgent(configuration);

        services.Configure<HackerNewsOptions>(configuration.GetSection("IdeaEngine:Sources:HackerNews"));
        services
            .AddHttpClient<HackerNewsAdapter>(client =>
            {
                client.BaseAddress = new Uri("https://hn.algolia.com/api/v1/");
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddStandardResilienceHandler();
        services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<HackerNewsAdapter>());

        services.Configure<FourChanOptions>(configuration.GetSection("IdeaEngine:Sources:FourChan"));
        services
            .AddHttpClient<FourChanAdapter>(client =>
            {
                client.BaseAddress = new Uri("https://a.4cdn.org/");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
            })
            .AddStandardResilienceHandler();
        services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<FourChanAdapter>());

        services.Configure<BlueskyOptions>(configuration.GetSection("IdeaEngine:Sources:Bluesky"));
        services
            .AddHttpClient<BlueskyAdapter>(client =>
            {
                client.BaseAddress = new Uri("https://public.api.bsky.app/");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
            })
            .AddStandardResilienceHandler();
        services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<BlueskyAdapter>());

        services.Configure<LemmyOptions>(configuration.GetSection("IdeaEngine:Sources:Lemmy"));
        services
            .AddHttpClient<LemmyAdapter>((sp, client) =>
            {
                var lemmy = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LemmyOptions>>().Value;
                client.BaseAddress = new Uri(lemmy.BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
            })
            .AddStandardResilienceHandler();
        services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<LemmyAdapter>());

        services.Configure<RedditRssOptions>(configuration.GetSection("IdeaEngine:Sources:RedditRss"));
        services
            .AddHttpClient<RedditRssAdapter>(client =>
            {
                client.BaseAddress = new Uri("https://www.reddit.com/");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
            })
            .AddStandardResilienceHandler();
        services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<RedditRssAdapter>());
    }

    private static string BuildUserAgent(IConfiguration configuration)
    {
        var redditUser = configuration["REDDIT_USERNAME"];
        return string.IsNullOrWhiteSpace(redditUser)
            ? "macos:idea-engine:v0.1 (personal research tool)"
            : $"macos:idea-engine:v0.1 (by /u/{redditUser})";
    }
}
