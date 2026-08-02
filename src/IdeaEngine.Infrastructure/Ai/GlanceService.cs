using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Ai;

/// <summary>One signal needing a glance line.</summary>
public sealed record GlanceInput(long SignalId, string Summary, string Kind);

/// <summary>
/// Lazily generates ≤10-word glance lines for ranking views. One batched nano call per
/// request covers only signals without a cached glance; results persist on the signal,
/// so repeat views are free. Failure degrades to the full summary - never blocks.
/// </summary>
public sealed class GlanceService(
    IdeaEngineDbContext db,
    OpenRouterChatClient chat,
    BudgetGuard budgetGuard,
    TimeProvider timeProvider,
    IOptions<GlanceOptions> glanceOptions,
    ILogger<GlanceService> logger)
{
    private const string StageName = "glance";

    private const string SystemPrompt =
        """
        You compress market signals into glanceable phrases. For every input line "id: text",
        produce a phrase of AT MOST 10 words that keeps the concrete need or product, no fluff,
        no trailing period. Reply with ONLY a JSON object:
        {"glances":[{"id":123,"text":"..."}]}
        """;

    public async Task<Dictionary<long, string>> EnsureGlancesAsync(
        IReadOnlyList<GlanceInput> inputs, CancellationToken cancellationToken)
    {
        var ids = inputs.Select(i => i.SignalId).ToList();
        var glances = await db.Signals
            .Where(s => ids.Contains(s.Id) && s.Glance != null)
            .ToDictionaryAsync(s => s.Id, s => s.Glance!, cancellationToken);

        var missing = inputs.Where(i => !glances.ContainsKey(i.SignalId)).ToList();
        if (missing.Count == 0 || !chat.IsConfigured)
        {
            return glances;
        }

        var options = glanceOptions.Value;
        var worstCall = (2_500m * options.InputPricePerMTok
            + options.MaxCompletionTokens * options.OutputPricePerMTok) / 1_000_000m;
        var check = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, worstCall, worstCall, cancellationToken);
        if (!check.Allowed)
        {
            logger.LogInformation("Glance generation skipped: {Reason}", check.Reason);
            return glances;
        }

        var completion = await chat.CompleteAsync(
            options.Model, SystemPrompt, BuildUserMessage(missing),
            options.MaxCompletionTokens, "low", cancellationToken);

        if (completion is { IsError: false })
        {
            db.AiLedger.Add(new AiLedgerEntry
            {
                Day = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime),
                Stage = StageName,
                Model = options.Model,
                TokensIn = completion.TokensIn,
                TokensOut = completion.TokensOut,
                CostUsd = (completion.TokensIn * options.InputPricePerMTok
                    + completion.TokensOut * options.OutputPricePerMTok) / 1_000_000m,
                CreatedAt = timeProvider.GetUtcNow(),
            });
        }

        var parsed = ParseResponse(completion?.Content);
        if (parsed.Count > 0)
        {
            var missingIds = missing.Select(m => m.SignalId).ToList();
            var entities = await db.Signals
                .Where(s => missingIds.Contains(s.Id))
                .ToListAsync(cancellationToken);

            foreach (var entity in entities)
            {
                if (parsed.TryGetValue(entity.Id, out var text))
                {
                    entity.Glance = text;
                    glances[entity.Id] = text;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return glances;
    }

    public static string BuildUserMessage(IReadOnlyList<GlanceInput> inputs)
    {
        var builder = new StringBuilder();
        foreach (var input in inputs)
        {
            builder.Append(input.SignalId.ToString(CultureInfo.InvariantCulture))
                .Append(": ").Append(input.Summary)
                .Append(" (").Append(input.Kind).Append(")\n");
        }

        return builder.ToString();
    }

    public static Dictionary<long, string> ParseResponse(string? content)
    {
        var dto = LlmJson.TryParse<GlanceResponseDto>(content);
        var result = new Dictionary<long, string>();
        foreach (var glance in dto?.Glances ?? [])
        {
            if (glance.Id > 0 && !string.IsNullOrWhiteSpace(glance.Text))
            {
                var text = glance.Text.Trim();
                result[glance.Id] = text.Length <= 160 ? text : text[..160];
            }
        }

        return result;
    }

    private sealed record GlanceResponseDto(
        [property: JsonPropertyName("glances")] IReadOnlyList<GlanceDto>? Glances);

    private sealed record GlanceDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("text")] string? Text);
}
