using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace IdeaEngine.Infrastructure.Ai;

/// <summary>Result of one chat completion, with usage for the ledger.</summary>
public sealed record ChatCompletion(string? Content, long TokensIn, long TokensOut, string? FinishReason);

/// <summary>
/// Generic single-turn OpenRouter chat call (JSON-object response format).
/// Model-agnostic: ideation and future stages share it. Never throws.
/// </summary>
public sealed class OpenRouterChatClient(
    HttpClient httpClient,
    ILogger<OpenRouterChatClient> logger)
{
    public bool IsConfigured => httpClient.DefaultRequestHeaders.Authorization is not null;

    public async Task<ChatCompletion?> CompleteAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        int maxCompletionTokens,
        string reasoningEffort,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
                response_format = new { type = "json_object" },
                max_tokens = maxCompletionTokens,
                reasoning = new { effort = reasoningEffort },
            };

            using var response = await httpClient.PostAsJsonAsync(
                new Uri("chat/completions", UriKind.Relative), request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var completion = await response.Content.ReadFromJsonAsync<CompletionResponse>(
                LlmJson.Options, cancellationToken);

            var choice = completion?.Choices is { Count: > 0 } choices ? choices[0] : null;
            return new ChatCompletion(
                choice?.Message?.Content,
                completion?.Usage?.PromptTokens ?? 0,
                completion?.Usage?.CompletionTokens ?? 0,
                choice?.FinishReason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Chat completion failed for model {Model}", model);
            return null;
        }
    }

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
}
