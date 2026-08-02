using System.Text.Json;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Common;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Ideation;

public sealed record IdeaRelation(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("kind")] string Kind);

public sealed record RelationCheckResult(IReadOnlyList<(long Id, string Kind, string Title)> Relations);

/// <summary>
/// Cheap semantic memory: when a new idea lands, a nano model compares it against recent
/// ideas and records duplicate/variant/related links on BOTH sides. Relations survive as
/// jsonb and show on cards - dismissed ideas stay findable through their relatives.
/// (Vector embeddings will replace the candidate scan later; the contract stays.)
/// </summary>
public sealed class RelationService(
    IdeaEngineDbContext db,
    OpenRouterChatClient chat,
    BudgetGuard budgetGuard,
    TimeProvider timeProvider,
    IOptions<GlanceOptions> nanoOptions,
    ILogger<RelationService> logger)
{
    private const string StageName = "relate";

    private const string SystemPrompt =
        """
        You link a NEW product idea to existing ones. Reply with ONLY a JSON object:
        {"relations":[{"id":123,"kind":"duplicate|variant|related"}]}
        Rules: "duplicate" = essentially the same idea; "variant" = same core with a changed
        angle/platform/audience; "related" = shares the problem space or could feed the other
        as a feature. Max 3 relations, only genuinely connected ones - an empty list is the
        normal answer. Ignore superficial keyword overlap.
        """;

    public async Task<RelationCheckResult> LinkAsync(long newIdeaId, CancellationToken cancellationToken)
    {
        var empty = new RelationCheckResult([]);
        if (!chat.IsConfigured)
        {
            return empty;
        }

        var idea = await db.Ideas.FindAsync([newIdeaId], cancellationToken);
        if (idea is null)
        {
            return empty;
        }

        var candidates = await db.Ideas
            .Where(i => i.Id != newIdeaId && i.Category != "meta")
            .OrderByDescending(i => i.Id)
            .Take(50)
            .Select(i => new { i.Id, i.Title, i.Thesis })
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return empty;
        }

        var options = nanoOptions.Value;
        var worstCall = (4_000m * options.InputPricePerMTok + 800 * options.OutputPricePerMTok) / 1_000_000m;
        var check = await budgetGuard.CheckAsync(StageName, 0.05m, worstCall, worstCall, cancellationToken);
        if (!check.Allowed)
        {
            logger.LogInformation("Relation check skipped: {Reason}", check.Reason);
            return empty;
        }

        var user = $"NEW idea #{idea.Id}: {idea.Title} — {TextClip.Clip(idea.Thesis, 200)}\n\nExisting ideas:\n"
            + string.Join('\n', candidates.Select(c => $"{c.Id}: {c.Title} — {TextClip.Clip(c.Thesis, 110)}"));

        var completion = await chat.CompleteAsync(
            options.Model, SystemPrompt, user, 800, "low", cancellationToken);

        if (completion is not null)
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

        var found = (LlmJson.TryParse<RelationsDto>(completion?.Content)?.Relations ?? [])
            .Where(r => r.Id > 0 && r.Id != newIdeaId)
            .Where(r => r.Kind is "duplicate" or "variant" or "related")
            .Take(3)
            .ToList();

        var results = new List<(long, string, string)>();
        foreach (var relation in found)
        {
            var other = await db.Ideas.FindAsync([relation.Id], cancellationToken);
            if (other is null)
            {
                continue;
            }

            AddRelation(idea, relation.Id, relation.Kind);
            AddRelation(other, idea.Id, relation.Kind);
            results.Add((other.Id, relation.Kind, other.Title));
        }

        await db.SaveChangesAsync(cancellationToken);
        if (results.Count > 0)
        {
            logger.LogInformation(
                "Idea #{IdeaId} linked: {Relations}",
                newIdeaId, string.Join(", ", results.Select(r => $"#{r.Item1}({r.Item2})")));
        }

        return new RelationCheckResult(results);
    }

    private static void AddRelation(IdeaEntity idea, long otherId, string kind)
    {
        var relations = string.IsNullOrWhiteSpace(idea.RelatedJson)
            ? []
            : JsonSerializer.Deserialize<List<IdeaRelation>>(idea.RelatedJson, LlmJson.Options) ?? [];
        if (relations.Any(r => r.Id == otherId))
        {
            return;
        }

        relations.Add(new IdeaRelation(otherId, kind));
        idea.RelatedJson = JsonSerializer.Serialize(relations, LlmJson.Options);
    }

    private sealed record RelationsDto(
        [property: JsonPropertyName("relations")] IReadOnlyList<IdeaRelation>? Relations);
}
