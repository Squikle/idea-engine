using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace IdeaEngine.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` tooling. Reads environment variables (source .env first when
/// applying migrations locally: see docs/RUNBOOK.md).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<IdeaEngineDbContext>
{
    public IdeaEngineDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        // `migrations add` never connects; use a placeholder password if none is set.
        var connectionString = configuration["POSTGRES_PASSWORD"] is null
            ? "Host=localhost;Port=5433;Database=ideaengine;Username=ideaengine;Password=design-time-only"
            : ConnectionStringBuilder.Resolve(configuration);

        var options = new DbContextOptionsBuilder<IdeaEngineDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new IdeaEngineDbContext(options);
    }
}
