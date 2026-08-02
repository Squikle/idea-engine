using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Ai;

/// <summary>
/// Triage via OpenRouter chat completions (OpenAI-compatible). One item per call,
/// JSON-object response format, one re-ask on malformed output, never throws.
/// </summary>
public sealed class OpenRouterTriageAnalyzer(
    HttpClient httpClient,
    IOptions<TriageOptions> triageOptions,
    ILogger<OpenRouterTriageAnalyzer> logger) : ITriageAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public bool IsConfigured => httpClient.DefaultRequestHeaders.Authorization is not null;

    public async Task<TriageOutcome> AnalyzeAsync(TriageInput input, CancellationToken cancellationToken)
    {
        var options = triageOptions.Value;
        var userMessage = TriagePrompt.BuildUserMessage(input, options);

        long tokensIn = 0;
        long tokensOut = 0;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var request = new
                {
                    model = options.Model,
                    messages = new object[]
                    {
                        new { role = "system", content = TriagePrompt.System },
                        new { role = "user", content = userMessage },
                    },
                    response_format = new { type = "json_object" },
                    max_tokens = options.MaxCompletionTokens,
                    reasoning = new { effort = options.ReasoningEffort },
                };

                using var response = await httpClient.PostAsJsonAsync(
                    new Uri("chat/completions", UriKind.Relative), request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var completion = await response.Content.ReadFromJsonAsync<CompletionResponse>(
                    JsonOptions, cancellationToken);

                tokensIn += completion?.Usage?.PromptTokens ?? 0;
                tokensOut += completion?.Usage?.CompletionTokens ?? 0;

                var choice = completion?.Choices is { Count: > 0 } choices ? choices[0] : null;
                if (TryParseVerdict(choice?.Message?.Content, out var verdict))
                {
                    return new TriageOutcome(verdict, tokensIn, tokensOut);
                }

                logger.LogWarning(
                    "Triage unparseable for item {ItemId} (attempt {Attempt}, finish={Finish}): {Preview}",
                    input.ItemId, attempt, choice?.FinishReason ?? "?",
                    Preview(choice?.Message?.Content));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Triage call failed for item {ItemId} (attempt {Attempt})", input.ItemId, attempt);
            }
        }

        return new TriageOutcome(null, tokensIn, tokensOut);
    }

    private static bool TryParseVerdict(string? content, out TriageVerdict verdict)
    {
        verdict = new TriageVerdict(0, "en", []);
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        // Models occasionally wrap JSON in code fences despite instructions.
        var json = content.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var start = json.IndexOf('{', StringComparison.Ordinal);
            var end = json.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return false;
            }

            json = json[start..(end + 1)];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<VerdictDto>(json, JsonOptions);
            if (parsed is null)
            {
                return false;
            }

            var signals = (parsed.Signals ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s.Summary) && !string.IsNullOrWhiteSpace(s.Kind))
                .Take(5)
                .Select(s => new SignalDraft(
                    s.Kind!.Trim().ToLowerInvariant(),
                    s.Summary!.Trim(),
                    string.IsNullOrWhiteSpace(s.Audience) ? null : s.Audience.Trim(),
                    string.IsNullOrWhiteSpace(s.CommercialSentiment)
                        ? "nice_to_have"
                        : s.CommercialSentiment.Trim().ToLowerInvariant(),
                    Clamp(s.Novelty),
                    Clamp(s.Confidence)))
                .ToList();

            verdict = new TriageVerdict(
                Clamp(parsed.Relevance),
                string.IsNullOrWhiteSpace(parsed.Language) ? "en" : parsed.Language.Trim().ToLowerInvariant(),
                signals);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double Clamp(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);

    private static string Preview(string? content) =>
        string.IsNullOrEmpty(content) ? "<empty>" : content.Length <= 150 ? content : content[..150];

    private sealed record CompletionResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<Choice>? Choices,
        [property: JsonPropertyName("usage")] Usage? Usage);

    private sealed record Choice(
        [property: JsonPropertyName("message")] Message? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record Message([property: JsonPropertyName("content")] string? Content);

    private sealed record Usage(
        [property: JsonPropertyName("prompt_tokens")] long PromptTokens,
        [property: JsonPropertyName("completion_tokens")] long CompletionTokens);

    private sealed record VerdictDto(
        [property: JsonPropertyName("relevance")] double Relevance,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("signals")] IReadOnlyList<SignalDto>? Signals);

    private sealed record SignalDto(
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("audience")] string? Audience,
        [property: JsonPropertyName("commercial_sentiment")] string? CommercialSentiment,
        [property: JsonPropertyName("novelty")] double Novelty,
        [property: JsonPropertyName("confidence")] double Confidence);
}
