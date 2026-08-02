namespace IdeaEngine.Core.Sources;

/// <summary>
/// Read adapter for one external source. Implementations live in Infrastructure.
/// Contract expectations:
/// <list type="bullet">
///   <item>Rate-limit aware (respect the source's published limits).</item>
///   <item>Partial-failure tolerant: a bad item is logged and skipped, never thrown.</item>
///   <item>Yields items incrementally; callers decide persistence and filtering.</item>
/// </list>
/// </summary>
public interface ISourceAdapter
{
    SourceKind Kind { get; }

    IAsyncEnumerable<RawItem> FetchAsync(SourceFetchOptions options, CancellationToken cancellationToken);
}
