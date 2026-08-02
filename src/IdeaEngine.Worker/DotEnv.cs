namespace IdeaEngine.Worker;

/// <summary>
/// Minimal .env loader for host-mode development (`dotnet run`), so .env stays the single
/// source of truth shared with docker-compose. Real environment variables always win.
/// Searches the current directory, then up to four parents.
/// </summary>
internal static class DotEnv
{
    public static void Load()
    {
        var path = Locate();
        if (path is null)
        {
            return;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static string? Locate()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; directory is not null && depth < 5; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
