using System.Net.Http.Headers;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ingestion;
using IdeaEngine.Infrastructure.Notifications;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Sources.Bluesky;
using IdeaEngine.Infrastructure.Sources.FourChan;
using IdeaEngine.Infrastructure.Sources.HackerNews;
using IdeaEngine.Infrastructure.Sources.Lemmy;
using IdeaEngine.Infrastructure.Sources.RedditRss;
using IdeaEngine.Infrastructure.Sources.Gdelt;
using IdeaEngine.Infrastructure.Sources.YouTube;
using IdeaEngine.Infrastructure.Triage;
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

        // Owner-facing times render in this zone; storage stays UTC.
        var timeZoneId = configuration["IdeaEngine:TimeZone"] ?? "America/Toronto";
        services.TryAddSingleton(TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));

        services.AddDbContext<IdeaEngineDbContext>(dbOptions => dbOptions
            .UseNpgsql(
                ConnectionStringBuilder.Resolve(configuration),
                npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention());

        AddSourceAdapters(services, configuration);
        AddTelegram(services, configuration);
        AddAi(services, configuration);

        services.Configure<IngestionOptions>(configuration.GetSection("IdeaEngine:Ingestion"));
        services.AddScoped<IngestionService>();
        services.AddSingleton<IngestionCoordinator>();

        return services;
    }

    private static void AddAi(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TriageOptions>(configuration.GetSection("IdeaEngine:Ai:Triage"));
        services.Configure<IdeationOptions>(configuration.GetSection("IdeaEngine:Ai:Ideation"));
        services.Configure<AiBudgetOptions>(configuration.GetSection("IdeaEngine:Ai:Budget"));
        services.Configure<GlanceOptions>(configuration.GetSection("IdeaEngine:Ai:Glance"));
        services.Configure<Research.ResearchOptions>(configuration.GetSection("IdeaEngine:Ai:Research"));
        services.Configure<Research.AppealOptions>(configuration.GetSection("IdeaEngine:Ai:Appeal"));
        services.Configure<Research.DigOptions>(configuration.GetSection("IdeaEngine:Ai:Dig"));

        // LLM calls run far longer than the 10s default attempt timeout.
        services.AddHttpClient<OpenRouterTriageAnalyzer>(
                client => ConfigureOpenRouterClient(client, configuration))
            .AddStandardResilienceHandler(ConfigureLlmResilience);
        services.AddHttpClient<OpenRouterChatClient>(
                client => ConfigureOpenRouterClient(client, configuration))
            .AddStandardResilienceHandler(ConfigureLlmResilience);

        services.AddTransient<ITriageAnalyzer>(sp => sp.GetRequiredService<OpenRouterTriageAnalyzer>());
        services.AddScoped<BudgetGuard>();
        services.AddScoped<TriageService>();
        services.AddSingleton<TriageCoordinator>();
        services.AddScoped<Ideation.IdeationService>();
        services.AddScoped<GlanceService>();
        services.AddScoped<Ideation.RelationService>();

        services
            .AddHttpClient<Research.BraveSearchClient>(client =>
            {
                client.BaseAddress = new Uri("https://api.search.brave.com/res/v1/");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                var braveKey = configuration["BRAVE_API_KEY"];
                if (!string.IsNullOrWhiteSpace(braveKey))
                {
                    client.DefaultRequestHeaders.Add("X-Subscription-Token", braveKey);
                }
            })
            .AddStandardResilienceHandler();
        services
            .AddHttpClient<Research.PageFetcher>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", "Mozilla/5.0 (compatible; idea-engine/0.11; +https://github.com/Squikle/idea-engine)");
                client.MaxResponseContentBufferSize = 2_000_000;
            });
        services.AddScoped<Research.ResearchService>();
        services.AddScoped<Research.AppealService>();
        services.AddScoped<Research.DigService>();
        services.AddScoped<Maintenance.AuditService>();
        services.AddScoped<Maintenance.ReevalService>();
        services.Configure<Maintenance.ReevalOptions>(configuration.GetSection("IdeaEngine:Ai:Reeval"));
        services.AddSingleton<Research.ResearchCoordinator>();
        services.AddScoped<Reporting.DigestService>();
        services.AddScoped<Jobs.JobService>();
        services.AddScoped<Maintenance.RetentionService>();
        services.Configure<Autopilot.AutopilotOptions>(configuration.GetSection("IdeaEngine:Autopilot"));
        services.Configure<Maintenance.RetentionOptions>(configuration.GetSection("IdeaEngine:Retention"));
    }

    private static void ConfigureLlmResilience(Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions options)
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(100);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(110);
        options.Retry.MaxRetryAttempts = 1;
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(220);
    }

    private static void ConfigureOpenRouterClient(HttpClient client, IConfiguration configuration)
    {
        client.BaseAddress = new Uri(
            configuration["IdeaEngine:Ai:OpenRouterBaseUrl"] ?? "https://openrouter.ai/api/v1/");
        client.Timeout = TimeSpan.FromSeconds(120);

        var openRouterKey = configuration["OPENROUTER_API_KEY"];
        if (!string.IsNullOrWhiteSpace(openRouterKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openRouterKey);
        }

        // OpenRouter attribution headers (optional, recommended).
        client.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/Squikle/idea-engine");
        client.DefaultRequestHeaders.Add("X-Title", "idea-engine");
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
        services.AddSingleton<IAdviceJournal, FileAdviceJournal>();

        if (!telegram.IsConfigured)
        {
            services.AddSingleton<INotifier, NullNotifier>();
            services.AddSingleton<IStatusTracker, NullStatusTracker>();
            services.AddSingleton<IProgressNotifier, NullProgressNotifier>();
            return;
        }

        services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(telegram.BotToken!));
        services.AddSingleton<INotifier>(sp => new TelegramNotifier(
            sp.GetRequiredService<ITelegramBotClient>(),
            telegram.AdminChatId!.Value,
            sp.GetRequiredService<ILogger<TelegramNotifier>>()));
        services.AddSingleton<IStatusTracker>(sp => new TelegramStatusTracker(
            sp.GetRequiredService<ITelegramBotClient>(),
            telegram.AdminChatId!.Value,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<TimeZoneInfo>(),
            sp.GetRequiredService<ILogger<TelegramStatusTracker>>()));
        services.AddSingleton<IProgressNotifier>(sp => new TelegramProgressNotifier(
            sp.GetRequiredService<ITelegramBotClient>(),
            telegram.AdminChatId!.Value,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<TelegramProgressNotifier>>()));
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
        services.PostConfigure<BlueskyOptions>(o =>
        {
            o.Identifier ??= configuration["BLUESKY_IDENTIFIER"];
            o.AppPassword ??= configuration["BLUESKY_APP_PASSWORD"];
        });
        services
            .AddHttpClient<BlueskyAdapter>(client =>
            {
                client.BaseAddress = new Uri("https://bsky.social/");
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
            // Reddit answers 429 to bursts; rapid retries only dig the hole deeper.
            .AddStandardResilienceHandler(o => o.Retry.MaxRetryAttempts = 1);
        services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<RedditRssAdapter>());

        services.Configure<YouTubeOptions>(configuration.GetSection("IdeaEngine:Sources:YouTube"));
        services.PostConfigure<YouTubeOptions>(o => o.ApiKey ??= configuration["YOUTUBE_API_KEY"]);
        services
            .AddHttpClient<YouTubeAdapter>(client =>
            {
                client.BaseAddress = new Uri("https://www.googleapis.com/youtube/v3/");
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddStandardResilienceHandler();
        services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<YouTubeAdapter>());

        services.Configure<GdeltOptions>(configuration.GetSection("IdeaEngine:Sources:Gdelt"));
        services
            .AddHttpClient<GdeltAdapter>(client =>
            {
                client.BaseAddress = new Uri("https://api.gdeltproject.org/api/v2/doc/");
                client.Timeout = TimeSpan.FromSeconds(40);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
            })
            // GDELT routinely needs 10-25s per query; default 10s attempt timeout starved it.
            .AddStandardResilienceHandler(o =>
            {
                // No retries: a GDELT 429 means penalty box; repeats extend the sentence.
                o.Retry.MaxRetryAttempts = 0;
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(35);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(70);
            });
        services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<GdeltAdapter>());
    }

    private static string BuildUserAgent(IConfiguration configuration)
    {
        var redditUser = configuration["REDDIT_USERNAME"];
        return string.IsNullOrWhiteSpace(redditUser)
            ? "macos:idea-engine:v0.1 (personal research tool)"
            : $"macos:idea-engine:v0.1 (by /u/{redditUser})";
    }
}
