using IdeaEngine.Core.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IdeaEngine.Infrastructure.Notifications;

/// <summary>
/// Appends advisor output to journal/advice.md at the repo root (falls back to the
/// content root when no .git is found, e.g. in a container). Never throws.
/// </summary>
public sealed class FileAdviceJournal(
    IHostEnvironment environment,
    ILogger<FileAdviceJournal> logger) : IAdviceJournal, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<string> _path = new(() => ResolvePath(environment));

    public void Dispose() => _gate.Dispose();

    public async Task AppendAsync(string markdown, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = _path.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.AppendAllTextAsync(path, markdown.TrimEnd() + "\n\n", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Advice journal append failed (non-fatal)");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ResolvePath(IHostEnvironment environment)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return Path.Combine(directory.FullName, "journal", "advice.md");
            }
        }

        return Path.Combine(environment.ContentRootPath, "journal", "advice.md");
    }
}
