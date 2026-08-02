using IdeaEngine.Core.Pipeline;
using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Tests.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Tests.Triage;

public sealed class OpenRouterTriageAnalyzerTests
{
    private static readonly TriageInput Input = new(
        1, SourceKind.HackerNews, "ask_hn", "Why are hearing aids still $4000?",
        "My grandmother needs them.", 510, 301,
        [new RawComment("a", "OTC ones are junk, I'd pay $500 for good ones", 45)]);

    private const string GoodVerdict = """
        {"relevance":0.9,"language":"en","signals":[
          {"kind":"pain","summary":"Hearing aids cost $4000+ while users would pay $500 for decent quality",
           "audience":"people with hearing loss and their families",
           "commercial_sentiment":"genuine_need","novelty":0.4,"confidence":0.85}]}
        """;

    private static string Envelope(string content) =>
        "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(content) +
        "}}],\"usage\":{\"prompt_tokens\":700,\"completion_tokens\":180}}";

    private static OpenRouterTriageAnalyzer Create(StubHttpMessageHandler stub)
    {
        var httpClient = new HttpClient(stub) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        httpClient.DefaultRequestHeaders.Authorization = new("Bearer", "test");
        return new OpenRouterTriageAnalyzer(
            httpClient, Options.Create(new TriageOptions()), NullLogger<OpenRouterTriageAnalyzer>.Instance);
    }

    [Fact]
    public async Task AnalyzeAsync_ParsesVerdictAndUsage()
    {
        var stub = new StubHttpMessageHandler().Map("chat/completions", Envelope(GoodVerdict));
        var analyzer = Create(stub);

        var outcome = await analyzer.AnalyzeAsync(Input, CancellationToken.None);

        Assert.NotNull(outcome.Verdict);
        Assert.Equal(0.9, outcome.Verdict!.Relevance, 3);
        var signal = Assert.Single(outcome.Verdict.Signals);
        Assert.Equal("pain", signal.Kind);
        Assert.Equal("genuine_need", signal.CommercialSentiment);
        Assert.Equal(700, outcome.TokensIn);
        Assert.Equal(180, outcome.TokensOut);
    }

    [Fact]
    public async Task AnalyzeAsync_HandlesCodeFencedJson()
    {
        var fenced = "```json\n" + GoodVerdict + "\n```";
        var stub = new StubHttpMessageHandler().Map("chat/completions", Envelope(fenced));
        var analyzer = Create(stub);

        var outcome = await analyzer.AnalyzeAsync(Input, CancellationToken.None);

        Assert.NotNull(outcome.Verdict);
        Assert.Single(outcome.Verdict!.Signals);
    }

    [Fact]
    public async Task AnalyzeAsync_MalformedContent_ReturnsNullVerdictButKeepsUsage()
    {
        var stub = new StubHttpMessageHandler().Map("chat/completions", Envelope("sorry, I cannot"));
        var analyzer = Create(stub);

        var outcome = await analyzer.AnalyzeAsync(Input, CancellationToken.None);

        Assert.Null(outcome.Verdict);
        Assert.Equal(1400, outcome.TokensIn); // two attempts
    }

    [Fact]
    public async Task AnalyzeAsync_EmptySignals_IsValid()
    {
        var stub = new StubHttpMessageHandler().Map(
            "chat/completions", Envelope("""{"relevance":0.05,"language":"en","signals":[]}"""));
        var analyzer = Create(stub);

        var outcome = await analyzer.AnalyzeAsync(Input, CancellationToken.None);

        Assert.NotNull(outcome.Verdict);
        Assert.Empty(outcome.Verdict!.Signals);
    }
}
