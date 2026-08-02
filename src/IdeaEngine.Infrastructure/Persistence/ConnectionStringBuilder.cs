using Microsoft.Extensions.Configuration;

namespace IdeaEngine.Infrastructure.Persistence;

/// <summary>
/// Resolves the Postgres connection string with a single precedence rule:
/// explicit <c>ConnectionStrings:IdeaEngine</c> wins; otherwise it is composed from the
/// same POSTGRES_* variables docker-compose uses, so .env is the single source of truth.
/// </summary>
public static class ConnectionStringBuilder
{
    public static string Resolve(IConfiguration configuration)
    {
        var explicitConnectionString = configuration.GetConnectionString("IdeaEngine");
        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            return explicitConnectionString;
        }

        var host = configuration["DB_HOST"] ?? "localhost";
        var port = configuration["DB_PORT"] ?? "5433";
        var db = configuration["POSTGRES_DB"] ?? "ideaengine";
        var user = configuration["POSTGRES_USER"] ?? "ideaengine";
        var password = configuration["POSTGRES_PASSWORD"]
            ?? throw new InvalidOperationException(
                "No database password: set POSTGRES_PASSWORD (via .env) or ConnectionStrings:IdeaEngine.");

        return $"Host={host};Port={port};Database={db};Username={user};Password={password}";
    }
}
