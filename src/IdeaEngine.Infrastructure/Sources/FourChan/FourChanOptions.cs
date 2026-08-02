namespace IdeaEngine.Infrastructure.Sources.FourChan;

/// <summary>Bound from configuration section <c>IdeaEngine:Sources:FourChan</c>.</summary>
public sealed class FourChanOptions
{
    /// <summary>Boards to read (no slashes), e.g. "diy", "g".</summary>
    public IList<string> Boards { get; } = ["diy", "g"];

    /// <summary>Most-active threads taken per board per run.</summary>
    public int ThreadsPerBoard { get; set; } = 12;

    /// <summary>Threads with fewer replies are ignored (noise floor).</summary>
    public int MinReplies { get; set; } = 20;

    /// <summary>Replies stored per thread.</summary>
    public int CommentsPerThread { get; set; } = 12;

    /// <summary>Delay between API calls; 4chan asks for max 1 request/second.</summary>
    public int PolitenessDelayMs { get; set; } = 1100;
}
