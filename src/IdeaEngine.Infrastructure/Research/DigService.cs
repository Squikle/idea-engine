using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Notifications;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Research;

/// <summary>Bound from configuration section <c>IdeaEngine:Ai:Dig</c>.</summary>
public sealed class DigOptions
{
    public bool Enabled { get; set; } = true;

    public string Model { get; set; } = "anthropic/claude-sonnet-5";

    public decimal InputPricePerMTok { get; set; } = 2.00m;

    public decimal OutputPricePerMTok { get; set; } = 10.00m;

    public decimal DailyUsdCap { get; set; } = 1.00m;

    public int MaxBranches { get; set; } = 6;

    public int ResultsPerQuery { get; set; } = 5;

    public int SearchDelayMs { get; set; } = 1100;

    public int MaxCompletionTokens { get; set; } = 5000;
}

public sealed record DigRunResult(string Html, int SpawnedIdeas, decimal CostUsd, string? StoppedReason, IReadOnlyList<long> SpawnedIds);

/// <summary>
/// Niche excavation: topic → sub-niche tree → web evidence per branch → saturation map →
/// promising branches spawned as candidate ideas (origin "dig") ready for /research.
/// The same machinery handles pain-root digs ("/dig procrastination").
/// </summary>
public sealed class DigService(
    IdeaEngineDbContext db,
    OpenRouterChatClient chat,
    BraveSearchClient brave,
    BudgetGuard budgetGuard,
    TimeProvider timeProvider,
    IOptions<DigOptions> digOptions,
    ILogger<DigService> logger)
{
    private const string StageName = "dig";

    private const string PlanSystem =
        """
        You map a niche/topic into concrete sub-areas a garage-scale builder (1-3 people)
        could enter. Reply with ONLY a JSON object:
        {"branches":[{"name":"...","query":"concrete web search query","angle":"what to look for"}]}
        Rules: 4-6 branches spanning different entry types - physical products/accessories,
        apps/tools, services, content/community plays, and at least one arbitrage angle
        (underserved platform, country, or audience for proven demand in this niche).
        Queries in English, specific enough to surface real products and complaints.
        """;

    private static readonly string MapSystem =
        """
        You turn web evidence about a niche into an opportunity map for a garage-scale
        builder (1-3 people, apartment-buildable). Reply with ONLY a JSON object:
        {"map":[{"branch":"...","saturation":"low|medium|high","note":"1-2 sentences grounded in the results"}],
         "spawn":[{"title":"plain 3-6 word description","thesis":"2-3 sentences: what and why now",
                   "category":"saas|app|website|3dprint|hardware|wearable|service|content|reputation",
                   "target_user":"...","effort":1,"monetization":"...","distribution_note":"..."}]}
        Rules:
        - spawn 0-4 ideas, ONLY for branches where the evidence shows a real gap (low/medium
          saturation plus visible demand or complaints). An empty spawn list is an honest answer.
        - ARBITRAGE: incumbents ignoring a platform/country/audience dimension = a gap worth spawning.
        - No moralizing; record legal nuance inside the thesis when relevant.
        Lenses to think through:
        {LENSES}
        """.Replace("{LENSES}", Core.Pipeline.Playbooks.CompactList(), StringComparison.Ordinal);

    public async Task<DigRunResult> RunAsync(
        string topic, IProgressHandle? progress, CancellationToken cancellationToken)
    {
        var options = digOptions.Value;
        if (!options.Enabled || !chat.IsConfigured)
        {
            return new DigRunResult(string.Empty, 0, 0, "dig disabled or OPENROUTER_API_KEY missing", []);
        }

        if (!brave.IsConfigured)
        {
            return new DigRunResult(string.Empty, 0, 0, "BRAVE_API_KEY missing in .env", []);
        }

        var worstCall = (25_000m * options.InputPricePerMTok
            + options.MaxCompletionTokens * options.OutputPricePerMTok) / 1_000_000m;
        var check = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, worstCall, worstCall * 1.5m, cancellationToken);
        if (!check.Allowed)
        {
            return new DigRunResult(string.Empty, 0, 0, check.Reason, []);
        }

        decimal cost = 0;

        // 1. Plan the branch tree.
        if (progress is not null)
        {
            await progress.UpdateAsync($"planning sub-niches of “{TextClip.Clip(topic, 40)}”…", cancellationToken);
        }

        var planCompletion = await chat.CompleteAsync(
            options.Model, PlanSystem, $"Topic: {topic}", 1500, "low", cancellationToken);
        cost += RecordLedger(planCompletion, options);

        var branches = (LlmJson.TryParse<PlanDto>(planCompletion?.Content)?.Branches ?? [])
            .Where(b => !string.IsNullOrWhiteSpace(b.Query) && !string.IsNullOrWhiteSpace(b.Name))
            .Take(options.MaxBranches)
            .ToList();
        if (branches.Count == 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new DigRunResult(string.Empty, 0, cost, "branch planning returned nothing parseable", []);
        }

        // 2. Evidence per branch.
        var evidence = new StringBuilder($"Topic: {topic}\n");
        var searched = 0;
        foreach (var branch in branches)
        {
            if (progress is not null)
            {
                await progress.UpdateAsync(
                    $"searching {++searched}/{branches.Count}: {TextClip.Clip(branch.Name!, 40)}…", cancellationToken);
            }

            await Task.Delay(options.SearchDelayMs, cancellationToken);
            var hits = await brave.SearchAsync(branch.Query!, options.ResultsPerQuery, cancellationToken);

            evidence.Append("\n[").Append(branch.Name).Append("] angle: ").Append(branch.Angle).Append('\n');
            if (hits.Count == 0)
            {
                evidence.Append("(no results)\n");
                continue;
            }

            foreach (var hit in hits)
            {
                evidence.Append("- ").Append(hit.Title).Append(" | ").Append(hit.Url)
                    .Append(" | ").Append(hit.Description).Append('\n');
            }
        }

        // 3. Opportunity map + spawns.
        if (progress is not null)
        {
            await progress.UpdateAsync($"mapping opportunities from {branches.Count} branches…", cancellationToken);
        }

        MapDto? map = null;
        for (var attempt = 1; attempt <= 2 && map is null; attempt++)
        {
            var mapCompletion = await chat.CompleteAsync(
                options.Model, MapSystem, evidence.ToString(),
                options.MaxCompletionTokens, "medium", cancellationToken);
            cost += RecordLedger(mapCompletion, options);
            map = LlmJson.TryParse<MapDto>(mapCompletion?.Content);
        }

        if (map is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new DigRunResult(string.Empty, 0, cost, "opportunity mapping returned unparseable output twice", []);
        }

        // 4. Spawn candidate ideas.
        var now = timeProvider.GetUtcNow();
        var spawned = new List<IdeaEntity>();
        foreach (var spawn in (map.Spawn ?? []).Take(4))
        {
            if (string.IsNullOrWhiteSpace(spawn.Title))
            {
                continue;
            }

            var entity = new IdeaEntity
            {
                Title = TextClip.Clip(spawn.Title, 290),
                Thesis = $"{spawn.Thesis}\n(from /dig {topic})",
                Category = NormalizeCategory(spawn.Category),
                EffortScale = Math.Clamp(spawn.Effort, 1, 5),
                TargetUser = TextClip.Clip(spawn.TargetUser ?? string.Empty, 290),
                Monetization = TextClip.Clip(spawn.Monetization ?? string.Empty, 590),
                DistributionNote = TextClip.Clip(spawn.DistributionNote ?? string.Empty, 390),
                Status = "candidate",
                Origin = "dig",
                Playbook = "dig",
                BuilderModel = options.Model,
                CostUsd = 0,
                CreatedAt = now,
            };
            db.Ideas.Add(entity);
            spawned.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Dig '{Topic}': {Branches} branches, {Spawned} ideas spawned, ${Cost:F4}",
            topic, branches.Count, spawned.Count, cost);

        return new DigRunResult(
            FormatReport(topic, map, spawned, cost, branches.Count), spawned.Count, cost, null,
            [.. spawned.Select(s => s.Id)]);
    }

    private static string FormatReport(
        string topic, MapDto map, List<IdeaEntity> spawned, decimal cost, int branchCount)
    {
        var builder = new StringBuilder();
        builder.Append("⛏ <b>Dig · ").Append(WebUtility.HtmlEncode(TextClip.Clip(topic, 60))).Append("</b>\n");

        foreach (var entry in (map.Map ?? []).Take(6))
        {
            var icon = entry.Saturation?.ToLowerInvariant() switch
            {
                "low" => "🟢",
                "medium" => "🟡",
                _ => "🔴",
            };
            builder.Append(icon).Append(' ').Append(WebUtility.HtmlEncode(TextClip.Clip(entry.Branch ?? "?", 45)))
                .Append(" — ").Append(WebUtility.HtmlEncode(TextClip.Clip(entry.Note ?? string.Empty, 120)))
                .Append('\n');
        }

        if (spawned.Count > 0)
        {
            builder.Append("\n<b>🌱 Spawned ideas</b> (research them: /research ")
                .Append(string.Join(' ', spawned.Select(s => s.Id))).Append(")\n");
            foreach (var idea in spawned)
            {
                builder.Append("• #").Append(idea.Id).Append(" [").Append(idea.Category)
                    .Append("/e").Append(idea.EffortScale).Append("] ")
                    .Append(WebUtility.HtmlEncode(idea.Title)).Append('\n');
            }
        }
        else
        {
            builder.Append("\nNo gaps worth spawning — the niche looks covered. Honest answer.\n");
        }

        builder.Append('\n').Append(Ui.Spend).Append(" $").Append(cost.ToString("F3", CultureInfo.InvariantCulture))
            .Append(" · ").Append(branchCount).Append(" branches searched");
        return builder.ToString().TrimEnd();
    }

    private static string NormalizeCategory(string? category)
    {
        var value = category?.Trim().ToLowerInvariant();
        return value is "saas" or "app" or "website" or "3dprint" or "hardware" or "wearable"
            or "service" or "content" or "reputation"
            ? value
            : "other";
    }

    private decimal RecordLedger(ChatCompletion? completion, DigOptions options)
    {
        if (completion is null || completion.IsError)
        {
            return 0;
        }

        var cost = (completion.TokensIn * options.InputPricePerMTok
            + completion.TokensOut * options.OutputPricePerMTok) / 1_000_000m;
        db.AiLedger.Add(new AiLedgerEntry
        {
            Day = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime),
            Stage = StageName,
            Model = options.Model,
            TokensIn = completion.TokensIn,
            TokensOut = completion.TokensOut,
            CostUsd = cost,
            CreatedAt = timeProvider.GetUtcNow(),
        });
        return cost;
    }

    private sealed record PlanDto(
        [property: JsonPropertyName("branches")] IReadOnlyList<BranchDto>? Branches);

    private sealed record BranchDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("query")] string? Query,
        [property: JsonPropertyName("angle")] string? Angle);

    private sealed record MapDto(
        [property: JsonPropertyName("map")] IReadOnlyList<MapEntryDto>? Map,
        [property: JsonPropertyName("spawn")] IReadOnlyList<SpawnDto>? Spawn);

    private sealed record MapEntryDto(
        [property: JsonPropertyName("branch")] string? Branch,
        [property: JsonPropertyName("saturation")] string? Saturation,
        [property: JsonPropertyName("note")] string? Note);

    private sealed record SpawnDto(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("thesis")] string? Thesis,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("target_user")] string? TargetUser,
        [property: JsonPropertyName("effort")] int Effort,
        [property: JsonPropertyName("monetization")] string? Monetization,
        [property: JsonPropertyName("distribution_note")] string? DistributionNote);
}
