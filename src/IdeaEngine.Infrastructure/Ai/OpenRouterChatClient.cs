using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace IdeaEngine.Infrastructure.Ai;

/// <summary>One row of the live OpenRouter catalog ($ per MILLION tokens).</summary>
public sealed record CatalogModel(string Id, decimal InPerMTok, decimal OutPerMTok, long? ContextLength);

/// <summary>Result of one chat completion, with usage for the ledger.</summary>
public sealed record ChatCompletion(string? Content, long TokensIn, long TokensOut, string? FinishReason)
{
    /// <summary>True when this is a transport/API failure, not a model reply.</summary>
    public bool IsError => FinishReason?.StartsWith("error:", StringComparison.Ordinal) == true;
}

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
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var providerMessage = LlmJson.TryParse<ErrorEnvelope>(body)?.Error?.Message
                    ?? (body.Length > 140 ? body[..140] : body);
                var hint = (int)response.StatusCode == 402
                    ? " — OpenRouter credit balance exhausted; top up at openrouter.ai → Credits"
                    : string.Empty;
                logger.LogWarning(
                    "OpenRouter HTTP {Status} for {Model}: {Message}",
                    (int)response.StatusCode, model, providerMessage);
                return new ChatCompletion(
                    null, 0, 0, $"error: HTTP {(int)response.StatusCode} {providerMessage}{hint}");
            }

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
            return new ChatCompletion(null, 0, 0, $"error: {ex.GetType().Name}");
        }
    }

    private static readonly object CatalogLock = new();

    private static (DateTimeOffset At, IReadOnlyList<CatalogModel> Models)? _catalogCache;

    /// <summary>
    /// Live model catalog from OpenRouter (id + $/MTok), cached 1h. The owner can discover
    /// and adopt models without visiting the docs; /models set autofills prices from here.
    /// </summary>
    public async Task<IReadOnlyList<CatalogModel>?> ListModelsAsync(CancellationToken cancellationToken)
    {
        lock (CatalogLock)
        {
            if (_catalogCache is { } cached && cached.At > DateTimeOffset.UtcNow.AddHours(-1))
            {
                return cached.Models;
            }
        }

        try
        {
            var response = await httpClient.GetFromJsonAsync<ModelsEnvelope>(
                new Uri("models", UriKind.Relative), LlmJson.Options, cancellationToken);
            var models = (response?.Data ?? [])
                .Where(m => m.Id is { Length: > 0 } && m.Pricing is not null)
                .Select(m => new CatalogModel(
                    m.Id!,
                    ParsePerTokenPrice(m.Pricing!.Prompt) * 1_000_000m,
                    ParsePerTokenPrice(m.Pricing.Completion) * 1_000_000m,
                    m.ContextLength))
                .OrderBy(m => m.Id, StringComparer.Ordinal)
                .ToList();
            lock (CatalogLock)
            {
                _catalogCache = (DateTimeOffset.UtcNow, models);
            }

            return models;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Model catalog fetch failed");
            return null;
        }
    }

    private static decimal ParsePerTokenPrice(string? value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    /// <summary>Live prepaid balance: (deposited, used). Null when the endpoint is unreachable.</summary>
    public async Task<(decimal Total, decimal Used)?> GetCreditsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<CreditsEnvelope>(
                new Uri("credits", UriKind.Relative), LlmJson.Options, cancellationToken);
            return response?.Data is { } data ? (data.TotalCredits, data.TotalUsage) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Credits lookup failed");
            return null;
        }
    }

    private sealed record ModelsEnvelope([property: JsonPropertyName("data")] IReadOnlyList<ModelRow>? Data);

    private sealed record ModelRow(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("context_length")] long? ContextLength,
        [property: JsonPropertyName("pricing")] PricingRow? Pricing);

    private sealed record PricingRow(
        [property: JsonPropertyName("prompt")] string? Prompt,
        [property: JsonPropertyName("completion")] string? Completion);

    private sealed record ErrorEnvelope([property: JsonPropertyName("error")] ErrorBody? Error);

    private sealed record ErrorBody([property: JsonPropertyName("message")] string? Message);

    private sealed record CreditsEnvelope([property: JsonPropertyName("data")] CreditsBody? Data);

    private sealed record CreditsBody(
        [property: JsonPropertyName("total_credits")] decimal TotalCredits,
        [property: JsonPropertyName("total_usage")] decimal TotalUsage);

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
