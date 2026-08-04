using System.Text.Json;
using System.Text.Json.Serialization;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;

namespace IdeaEngine.Infrastructure.Ai;

/// <summary>Effective model for a stage: configured default or the owner's runtime override.</summary>
public sealed record ResolvedModel(string Model, decimal InPerMTok, decimal OutPerMTok, bool Overridden);

/// <summary>
/// Runtime model switching without code changes: /models set &lt;stage&gt; &lt;id&gt; [in] [out]
/// stores an override in app_state; every AI stage resolves through here at call time.
/// The owner can adopt a brand-new model the day it appears on OpenRouter.
/// </summary>
public sealed class ModelRegistry(IdeaEngineDbContext db, TimeProvider timeProvider)
{
    private const string KeyPrefix = "model.override.";

    /// <summary>Rough $/MTok for well-known models so /models set can omit prices.</summary>
    public static readonly IReadOnlyDictionary<string, (decimal In, decimal Out)> KnownPrices =
        new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai/gpt-5-nano"] = (0.05m, 0.40m),
            ["openai/gpt-5-mini"] = (0.25m, 2.00m),
            ["anthropic/claude-sonnet-5"] = (2.00m, 10.00m),
            ["anthropic/claude-opus-4.6"] = (5.00m, 25.00m),
            ["deepseek/deepseek-v4-pro"] = (0.43m, 0.87m),
            ["google/gemini-3-pro"] = (2.00m, 12.00m),
            ["google/gemini-3-flash"] = (0.30m, 2.00m),
            ["x-ai/grok-5"] = (3.00m, 15.00m),
        };

    public async Task<ResolvedModel> ResolveAsync(
        string stage, string defaultModel, decimal defaultIn, decimal defaultOut,
        CancellationToken cancellationToken)
    {
        var state = await db.AppState.FindAsync([KeyPrefix + stage], cancellationToken);
        if (state?.Value is { Length: > 0 } json
            && LlmJson.SafeDeserialize<OverrideDto>(json) is { Model.Length: > 0 } over)
        {
            return new ResolvedModel(over.Model, over.In ?? defaultIn, over.Out ?? defaultOut, true);
        }

        return new ResolvedModel(defaultModel, defaultIn, defaultOut, false);
    }

    public async Task SetAsync(
        string stage, string model, decimal? inPrice, decimal? outPrice,
        CancellationToken cancellationToken)
    {
        if (inPrice is null && KnownPrices.TryGetValue(model, out var known))
        {
            (inPrice, outPrice) = known;
        }

        var json = JsonSerializer.Serialize(
            new OverrideDto(model, inPrice, outPrice), LlmJson.Options);
        var key = KeyPrefix + stage;
        var state = await db.AppState.FindAsync([key], cancellationToken);
        if (state is null)
        {
            db.AppState.Add(new AppStateEntity
            {
                Key = key,
                Value = json,
                UpdatedAt = timeProvider.GetUtcNow(),
            });
        }
        else
        {
            state.Value = json;
            state.UpdatedAt = timeProvider.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ResetAsync(string stage, CancellationToken cancellationToken)
    {
        var state = await db.AppState.FindAsync([KeyPrefix + stage], cancellationToken);
        if (state is null)
        {
            return false;
        }

        db.AppState.Remove(state);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private sealed record OverrideDto(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("in")] decimal? In,
        [property: JsonPropertyName("out")] decimal? Out);
}
