using IdeaEngine.Infrastructure.Ai;

namespace IdeaEngine.Tests.Ai;

public sealed class LlmDiagTests
{
    [Fact]
    public void Describe_Null_IsTransport()
    {
        Assert.Contains("no response", LlmDiag.Describe(null), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_HttpError_PassesProviderMessageThrough()
    {
        var completion = new ChatCompletion(null, 0, 0,
            "error: HTTP 402 Insufficient credits — OpenRouter credit balance exhausted; top up at openrouter.ai → Credits");

        Assert.True(completion.IsError);
        Assert.Contains("402", LlmDiag.Describe(completion), StringComparison.Ordinal);
        Assert.Contains("top up", LlmDiag.Describe(completion), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_Truncation_NamesTheFixAndFlagsRetryable()
    {
        var completion = new ChatCompletion("{\"partial\":", 2000, 3000, "length");

        Assert.True(LlmDiag.IsTruncation(completion));
        Assert.Contains("truncated at 3000 tokens", LlmDiag.Describe(completion), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ProseInsteadOfJson_ShowsPreview()
    {
        var completion = new ChatCompletion("I'm sorry, I can't help with that request.", 500, 20, "stop");

        Assert.False(LlmDiag.IsTruncation(completion));
        var diag = LlmDiag.Describe(completion);
        Assert.Contains("not the expected JSON", diag, StringComparison.Ordinal);
        Assert.Contains("I'm sorry", diag, StringComparison.Ordinal);
    }
}
