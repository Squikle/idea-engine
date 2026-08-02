using IdeaEngine.Core.Common;

namespace IdeaEngine.Infrastructure.Ai;

/// <summary>
/// Turns a failed/odd completion into a diagnosis a human can act on.
/// "unparseable" is banned vocabulary - say WHAT happened and WHAT to do.
/// </summary>
public static class LlmDiag
{
    public static string Describe(ChatCompletion? completion)
    {
        if (completion is null)
        {
            return "no response from the model API (transport failure) — retry usually works";
        }

        if (completion.IsError)
        {
            return completion.FinishReason!; // already "error: HTTP 402 … top up at openrouter.ai"
        }

        if (string.Equals(completion.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            return $"output truncated at {completion.TokensOut} tokens (finish=length) — " +
                "the token budget was too small for this reply";
        }

        if (string.IsNullOrWhiteSpace(completion.Content))
        {
            return $"empty reply (finish={completion.FinishReason ?? "?"}, {completion.TokensOut} tokens out)";
        }

        return $"reply was not the expected JSON (finish={completion.FinishReason ?? "?"}): " +
            $"“{TextClip.Clip(completion.Content, 90)}”";
    }

    /// <summary>True when a retry with a bigger completion budget could fix it.</summary>
    public static bool IsTruncation(ChatCompletion? completion) =>
        completion is { IsError: false }
        && string.Equals(completion.FinishReason, "length", StringComparison.OrdinalIgnoreCase);
}
