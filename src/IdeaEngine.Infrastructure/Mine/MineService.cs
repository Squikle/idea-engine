using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Sources;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Mine;

/// <summary>Bound from configuration section <c>IdeaEngine:Ai:Mine</c>.</summary>
public sealed class MineOptions
{
    public bool Enabled { get; set; } = true;

    public string Model { get; set; } = "anthropic/claude-sonnet-5";

    public decimal InputPricePerMTok { get; set; } = 2.00m;

    public decimal OutputPricePerMTok { get; set; } = 10.00m;

    public decimal DailyUsdCap { get; set; } = 0.60m;

    public int MaxCompletionTokens { get; set; } = 1800;

    public MineOptions WithModel(ResolvedModel resolved)
    {
        var clone = (MineOptions)MemberwiseClone();
        clone.Model = resolved.Model;
        clone.InputPricePerMTok = resolved.InPerMTok;
        clone.OutputPricePerMTok = resolved.OutPerMTok;
        return clone;
    }
}

public sealed record MineResult(string Html, int SignalsStored, decimal CostUsd, string? StoppedReason);

/// <summary>
/// AI as a source: models digested years of forums, reviews, books and complaints we
/// can never crawl - /mine asks them directly what people struggle with, through named
/// ANGLES (rotated daily, listed, extensible) or the operator's free-text fantasy.
/// Mined pains land as signals feeding normal ideation; replies continue the dialogue.
/// </summary>
public sealed class MineService(
    IdeaEngineDbContext db,
    OpenRouterChatClient chat,
    BudgetGuard budgetGuard,
    TimeProvider timeProvider,
    IOptions<MineOptions> mineOptions,
    ModelRegistry models,
    ILogger<MineService> logger)
{
    private const string StageName = "mine";
    private const string RotationKey = "mine.rotation";

    /// <summary>Named angles - each is a different question, so we learn what works.</summary>
    public static readonly IReadOnlyList<(string Key, string Prompt)> Angles =
    [
        ("devpain", "What do software developers privately struggle with and complain about to each other, that tooling still doesn't solve?"),
        ("boringmoney", "Which boring, unsexy niches quietly pay for small tools because nobody builds for them?"),
        ("weeklyhate", "What recurring weekly tasks do small business owners hate enough to pay a few dollars monthly to avoid?"),
        ("newlypossible", "What became newly POSSIBLE for a solo developer this year (AI capability shifts included) that wasn't feasible two years ago - where no one has built the obvious product yet?"),
        ("fivedollar", "What would ordinary people happily pay $5/month for if it just existed and worked, judging by years of complaints?"),
        ("hobbygap", "Which passionate hobby communities are underserved by software - where enthusiasm is high and tools are ancient?"),
        ("worksucks", "What do office workers complain about their daily workflows that a tiny tool could fix - not enterprise software, a personal tool?"),
        ("nichehardware", "Where do people bodge physical solutions (3D prints, Arduino hacks, weird purchases) because no proper cheap product exists?"),
        ("parentpain", "What do parents repeatedly say is harder than it should be, that an app or gadget at garage scale could genuinely ease?"),
        ("aftermarket", "Which popular products/platforms have furious users begging for a missing companion feature the vendor ignores?"),
    ];

    public async Task<MineResult> RunAsync(
        string? operatorSeed, IReadOnlyList<(string Role, string Content)>? history,
        CancellationToken cancellationToken)
    {
        var baseOptions = mineOptions.Value;
        var options = baseOptions.WithModel(await models.ResolveAsync(
            StageName, baseOptions.Model, baseOptions.InputPricePerMTok,
            baseOptions.OutputPricePerMTok, cancellationToken));
        if (!options.Enabled || !chat.IsConfigured)
        {
            return new MineResult(string.Empty, 0, 0, "mine disabled or OPENROUTER_API_KEY missing");
        }

        var worstCall = (6_000m * options.InputPricePerMTok
            + options.MaxCompletionTokens * options.OutputPricePerMTok) / 1_000_000m;
        var check = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, worstCall, worstCall, cancellationToken);
        if (!check.Allowed)
        {
            return new MineResult(string.Empty, 0, 0, check.Reason);
        }

        // Angle: operator seed wins; otherwise rotate so consecutive runs differ.
        string angleKey;
        string question;
        if (operatorSeed is { Length: > 0 })
        {
            angleKey = "custom";
            question = operatorSeed;
        }
        else
        {
            var index = await NextRotationIndexAsync(cancellationToken);
            (angleKey, question) = Angles[index % Angles.Count];
        }

        // Ground with a sample of live signals so the model anchors to observed reality.
        var sample = await db.Signals
            .OrderByDescending(s => s.Id)
            .Take(12)
            .Select(s => s.Summary)
            .ToListAsync(cancellationToken);

        var user = new StringBuilder();
        user.Append("Today's date: ").Append(timeProvider.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append(".\nQUESTION (angle '").Append(angleKey).Append("'): ").Append(question).Append('\n');
        if (sample.Count > 0)
        {
            user.Append("\nFor grounding, pains we observed live this week (do NOT repeat these - go where they point):\n");
            foreach (var line in sample)
            {
                user.Append("- ").Append(TextClip.Clip(line, 140)).Append('\n');
            }
        }

        if (history is { Count: > 0 })
        {
            user.Append("\nCONVERSATION SO FAR (continue it, don't restart):\n");
            foreach (var (role, content) in history.TakeLast(8))
            {
                user.Append(role).Append(": ").Append(TextClip.Clip(content, 700)).Append('\n');
            }
        }

        var completion = await chat.CompleteAsync(
            options.Model, SystemPrompt, user.ToString(),
            options.MaxCompletionTokens, "medium", cancellationToken);

        decimal cost = 0;
        if (completion is { IsError: false })
        {
            cost = (completion.TokensIn * options.InputPricePerMTok
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
        }

        var parsed = LlmJson.TryParse<MineReplyDto>(completion?.Content);
        if (parsed is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new MineResult(string.Empty, 0, cost, $"mine failed — {LlmDiag.Describe(completion)}");
        }

        // Persist pains as signals via one anchor raw_item (provenance: SourceKind.AiMine).
        var pains = (parsed.Pains ?? []).Where(p => p.Pain is { Length: > 10 }).Take(8).ToList();
        var stored = 0;
        if (pains.Count > 0)
        {
            var stamp = timeProvider.GetUtcNow();
            var anchor = new RawItemEntity
            {
                Source = SourceKind.AiMine,
                ExternalId = ContentHasher.Compute($"mine|{angleKey}|{stamp:yyyyMMddHHmm}"),
                Title = $"mine[{angleKey}]: {TextClip.Clip(question, 120)}",
                Body = parsed.Commentary,
                Community = angleKey,
                ContentHash = ContentHasher.Compute(question, string.Join('\n', pains.Select(p => p.Pain))),
                Status = ItemStatus.Triaged, // the signals below ARE the triage output
                FetchedAt = stamp,
                CreatedAt = stamp,
            };
            db.RawItems.Add(anchor);
            await db.SaveChangesAsync(cancellationToken);

            foreach (var pain in pains)
            {
                db.Signals.Add(new SignalEntity
                {
                    RawItemId = anchor.Id,
                    Kind = "pain",
                    Summary = pain.Pain!,
                    Audience = pain.Who,
                    CommercialSentiment = pain.Sentiment is "buys_despite_complaints" or "genuine_need" or "nice_to_have" or "no_market"
                        ? pain.Sentiment
                        : "genuine_need",
                    Novelty = 0.6,
                    Confidence = 0.55, // model memory, not live evidence - triage humility
                    Model = options.Model,
                    CreatedAt = stamp,
                });
                stored++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Mine[{Angle}]: {Stored} pains stored, ${Cost:F4}", angleKey, stored, cost);

        var html = new StringBuilder();
        html.Append("⛏ <b>MINE · ").Append(angleKey).Append("</b>\n<i>")
            .Append(System.Net.WebUtility.HtmlEncode(TextClip.Clip(question, 200))).Append("</i>\n\n");
        foreach (var pain in pains)
        {
            html.Append("• <b>").Append(System.Net.WebUtility.HtmlEncode(pain.Pain!)).Append("</b>");
            if (pain.Who is { Length: > 0 })
            {
                html.Append(" — <i>").Append(System.Net.WebUtility.HtmlEncode(pain.Who)).Append("</i>");
            }

            if (pain.Context is { Length: > 0 })
            {
                html.Append('\n').Append(System.Net.WebUtility.HtmlEncode(pain.Context));
            }

            html.Append('\n');
        }

        if (parsed.Commentary is { Length: > 0 })
        {
            html.Append('\n').Append(System.Net.WebUtility.HtmlEncode(parsed.Commentary)).Append('\n');
        }

        html.Append('\n').Append(stored).Append(" signals stored (feed ideation) · ")
            .Append(Ui.Spend).Append(" $").Append(cost.ToString("F3", CultureInfo.InvariantCulture))
            .Append("\n<i>reply to this message to keep digging · /mine list for angles</i>");
        return new MineResult(html.ToString(), stored, cost, null);
    }

    private async Task<int> NextRotationIndexAsync(CancellationToken cancellationToken)
    {
        var state = await db.AppState.FindAsync([RotationKey], cancellationToken);
        var next = state is not null && int.TryParse(state.Value, out var current) ? current + 1 : 0;
        if (state is null)
        {
            db.AppState.Add(new AppStateEntity
            {
                Key = RotationKey,
                Value = "0",
                UpdatedAt = timeProvider.GetUtcNow(),
            });
        }
        else
        {
            state.Value = next.ToString(CultureInfo.InvariantCulture);
            state.UpdatedAt = timeProvider.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
        return next;
    }

    private const string SystemPrompt =
        """
        You mine your own trained knowledge - years of forums, reviews, complaint threads,
        niche communities and books - for CONCRETE pains a 1-3 person team could address.
        Stay grounded: no moonshots, no "AI platform for everything", no flying cars. Each
        pain must be something real people demonstrably complain about or pay to avoid.
        Edgy/gray niches are valid; do not moralize. Be concise and concrete.
        Reply with ONLY a JSON object:
        {"pains":[{"pain":"one concrete sentence, understandable alone","who":"exact audience",
          "context":"1-2 sentences: where this shows up, what people do today, why current options fail",
          "sentiment":"buys_despite_complaints|genuine_need|nice_to_have"}],
         "commentary":"2-3 sentences: the pattern you notice, or a follow-up question to the operator"}
        5-8 pains. When continuing a conversation, go DEEPER on what the operator asked,
        don't restart from zero.
        """;

    private sealed record MineReplyDto(
        [property: JsonPropertyName("pains")] List<MinePainDto>? Pains,
        [property: JsonPropertyName("commentary")] string? Commentary);

    private sealed record MinePainDto(
        [property: JsonPropertyName("pain")] string? Pain,
        [property: JsonPropertyName("who")] string? Who,
        [property: JsonPropertyName("context")] string? Context,
        [property: JsonPropertyName("sentiment")] string? Sentiment);
}
