using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IdeaEngine.Core.Common;
using IdeaEngine.Infrastructure.Ai;
using IdeaEngine.Infrastructure.Ideation;
using IdeaEngine.Infrastructure.Persistence;
using IdeaEngine.Infrastructure.Persistence.Entities;
using IdeaEngine.Infrastructure.Research;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdeaEngine.Infrastructure.Hand;

/// <summary>Bound from configuration section <c>IdeaEngine:Ai:Hand</c>.</summary>
public sealed class HandOptions
{
    public bool Enabled { get; set; } = true;

    public string Model { get; set; } = "anthropic/claude-opus-4.6";

    public decimal InputPricePerMTok { get; set; } = 5.00m;

    public decimal OutputPricePerMTok { get; set; } = 25.00m;

    public decimal DailyUsdCap { get; set; } = 2.50m;

    public int MaxCompletionTokens { get; set; } = 2200;

    /// <summary>Read-intent round-trips per message; keeps one question from looping cost.</summary>
    public int MaxToolHops { get; set; } = 4;

    public HandOptions WithModel(ResolvedModel resolved)
    {
        var clone = (HandOptions)MemberwiseClone();
        clone.Model = resolved.Model;
        clone.InputPricePerMTok = resolved.InPerMTok;
        clone.OutputPricePerMTok = resolved.OutPerMTok;
        return clone;
    }
}

/// <summary>One proposed write: shown in the audit card, executed by CODE after approval.</summary>
public sealed record HandWrite(
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("ids")] List<long>? Ids,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record HandTurn(string Say, IReadOnlyList<HandWrite> Writes, decimal CostUsd, string? StoppedReason);

/// <summary>
/// The right hand: a conversational brain over the WHOLE database. It never touches
/// data itself - it emits READ intents (executed instantly by code, results fed back)
/// and WRITE proposals (rendered as an audit card; code executes only after the owner
/// taps Apply). AI is the brain; code is the only executor - the owner's law.
/// </summary>
public sealed class HandService(
    IdeaEngineDbContext db,
    OpenRouterChatClient chat,
    BudgetGuard budgetGuard,
    TimeProvider timeProvider,
    IOptions<HandOptions> handOptions,
    ModelRegistry models,
    ILogger<HandService> logger)
{
    private const string StageName = "hand";

    public const string SessionKey = "hand.session";

    private const string SystemPrompt =
        """
        You are the operator's right hand inside his idea-engine (a personal product-opportunity
        pipeline: sources → signals → ideas → skeptic → research with concern ledger → appeal →
        partner). You chat about ANYTHING in its database and propose changes.
        STRICT PROTOCOL - reply with ONLY a JSON object:
        {"say":"your conversational reply (plain text, no markdown headers)",
         "reads":[{"tool":"...","...":...}] or [],
         "writes":[{"action":"...","...":...}] or []}
        READ TOOLS (executed immediately, results come back to you as TOOL RESULTS):
        - {"tool":"list_ideas","filter":"top|hot|uncertain|new|fresh|stale|dead|all","limit":20}
        - {"tool":"get_idea","id":69}                    ← full trail: pitch, skeptic, research, concerns, appeal, notes, partner
        - {"tool":"find_ideas","text":"car display"}      ← fuzzy title/thesis search
        - {"tool":"portfolio_stats"}
        - {"tool":"list_signals","limit":15}
        - {"tool":"list_models"}
        WRITE ACTIONS (NEVER executed directly - they become an audit card the owner must approve):
        - {"action":"set_status","id":4,"status":"dismissed|hot|candidate|uncertain","reason":"..."}
        - {"action":"add_note","id":69,"text":"..."}
        - {"action":"queue_research","ids":[24,48]}
        - {"action":"run_appeal","id":57}
        - {"action":"run_partner","id":69}
        - {"action":"set_model","stage":"research","model":"vendor/id"}
        - {"action":"set_setting","key":"sessions_per_day|auto_research_top|min_rating_for_research","value":"5"}
        RULES:
        - Look before you talk: when asked about specific ideas, READ them first (up to 4 hops).
        - Scores are EARNED through judgment - there is no score-edit action by design; when a
          score looks wrong, propose run_appeal instead.
        - Propose writes only with a clear reason; killing something needs the same rigor the
          skeptic applies. Never invent data you didn't read.
        - Be a partner, not a yes-man: disagree when the data disagrees.
        - Keep "say" tight; you're in a Telegram chat, not writing a report.
        """;

    public async Task<HandTurn> TurnAsync(
        string userMessage, CancellationToken cancellationToken)
    {
        var baseOptions = handOptions.Value;
        var options = baseOptions.WithModel(await models.ResolveAsync(
            StageName, baseOptions.Model, baseOptions.InputPricePerMTok,
            baseOptions.OutputPricePerMTok, cancellationToken));
        if (!options.Enabled || !chat.IsConfigured)
        {
            return new HandTurn(string.Empty, [], 0, "hand disabled or OPENROUTER_API_KEY missing");
        }

        var worstCall = (18_000m * options.InputPricePerMTok
            + options.MaxCompletionTokens * options.OutputPricePerMTok) / 1_000_000m;
        var check = await budgetGuard.CheckAsync(
            StageName, options.DailyUsdCap, worstCall,
            worstCall * (1 + options.MaxToolHops), cancellationToken);
        if (!check.Allowed)
        {
            return new HandTurn(string.Empty, [], 0, check.Reason);
        }

        var history = await LoadSessionAsync(cancellationToken);
        history.Add(("operator", userMessage));

        decimal cost = 0;
        HandReplyDto? reply = null;
        var transcript = BuildTranscript(history);
        for (var hop = 0; hop <= options.MaxToolHops; hop++)
        {
            var completion = await chat.CompleteAsync(
                options.Model, SystemPrompt, transcript,
                options.MaxCompletionTokens, "medium", cancellationToken);
            cost += RecordLedger(completion, options);

            reply = LlmJson.TryParse<HandReplyDto>(completion?.Content);
            if (reply is null)
            {
                await db.SaveChangesAsync(cancellationToken);
                return new HandTurn(string.Empty, [], cost, $"hand failed — {LlmDiag.Describe(completion)}");
            }

            if (reply.Reads is not { Count: > 0 } || hop == options.MaxToolHops)
            {
                break;
            }

            var results = new StringBuilder("TOOL RESULTS:\n");
            foreach (var read in reply.Reads.Take(5))
            {
                results.Append(await ExecuteReadAsync(read, cancellationToken)).Append('\n');
            }

            transcript = transcript + "\nassistant(proposed reads): "
                + JsonSerializer.Serialize(reply.Reads, LlmJson.Options)
                + "\n" + results;
        }

        var say = reply?.Say?.Trim() ?? string.Empty;
        var writes = (reply?.Writes ?? []).Where(w => w.Action is { Length: > 0 }).Take(8).ToList();

        history.Add(("hand", say + (writes.Count > 0
            ? $" [proposed {writes.Count} change(s): {string.Join(", ", writes.Select(w => w.Action))}]"
            : string.Empty)));
        await SaveSessionAsync(history, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Hand turn: {Writes} writes proposed, ${Cost:F4}", writes.Count, cost);
        return new HandTurn(say, writes, cost, null);
    }

    public async Task ClearSessionAsync(CancellationToken cancellationToken)
    {
        var state = await db.AppState.FindAsync([SessionKey], cancellationToken);
        if (state is not null)
        {
            db.AppState.Remove(state);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Human-readable audit line for one proposed write (the Apply card).</summary>
    public static string Describe(HandWrite write) => write.Action switch
    {
        "set_status" => $"set #{write.Id} → {write.Status}" + WithReason(write),
        "add_note" => $"note on #{write.Id}: “{TextClip.Clip(write.Text ?? string.Empty, 100)}”",
        "queue_research" => $"queue research for {string.Join(", ", (write.Ids ?? (write.Id is { } id ? [id] : new List<long>())).Select(i => $"#{i}"))}",
        "run_appeal" => $"run appeal on #{write.Id}" + WithReason(write),
        "run_partner" => $"run partner take on #{write.Id}",
        "set_model" => $"model: {write.Stage} → {write.Model}",
        "set_setting" => $"setting: {write.Key} → {write.Value}",
        _ => $"UNKNOWN action '{write.Action}' (will be skipped)",
    };

    private static string WithReason(HandWrite write) =>
        write.Reason is { Length: > 0 } ? $" — {TextClip.Clip(write.Reason, 120)}" : string.Empty;

    private async Task<string> ExecuteReadAsync(HandRead read, CancellationToken cancellationToken)
    {
        try
        {
            switch (read.Tool)
            {
                case "list_ideas":
                {
                    var limit = Math.Clamp(read.Limit ?? 20, 1, 40);
                    var ideas = await db.Ideas
                        .Where(i => i.Category != "meta")
                        .OrderByDescending(i => i.Id)
                        .Take(300)
                        .ToListAsync(cancellationToken);
                    var filtered = read.Filter switch
                    {
                        "hot" => ideas.Where(i => i.Status == "hot"),
                        "uncertain" => ideas.Where(i => i.Status is "uncertain" or "validated"),
                        "new" => ideas.Where(i => i.Status == "candidate"),
                        "fresh" => ideas.Where(i => i.CreatedAt >= timeProvider.GetUtcNow().AddHours(-48)),
                        "dead" => ideas.Where(i => i.Status == "dismissed"),
                        "top" => ideas.Where(i => i.Status != "dismissed"),
                        _ => ideas,
                    };
                    var rows = filtered
                        .Select(i => new { i.Id, i.Title, i.Status, i.Origin, Rating = Math.Round(IdeaScores.Rating(i), 2), Notes = i.NotesJson != null })
                        .OrderByDescending(x => x.Rating)
                        .Take(limit);
                    return "list_ideas: " + JsonSerializer.Serialize(rows, LlmJson.Options);
                }

                case "get_idea":
                {
                    var idea = await db.Ideas.FindAsync([read.Id ?? 0], cancellationToken);
                    if (idea is null)
                    {
                        return $"get_idea: no idea #{read.Id}";
                    }

                    var report = await db.ResearchReports
                        .Where(r => r.IdeaId == idea.Id)
                        .OrderByDescending(r => r.Id)
                        .Select(r => new { r.Verdict, r.Confidence, r.EngineVersion, r.ReportJson, r.CreatedAt })
                        .FirstOrDefaultAsync(cancellationToken);
                    return "get_idea: " + JsonSerializer.Serialize(new
                    {
                        idea.Id,
                        idea.Title,
                        idea.Status,
                        idea.Origin,
                        idea.Thesis,
                        pitch = idea.OriginalPitch,
                        idea.TargetUser,
                        idea.Monetization,
                        rating = Math.Round(IdeaScores.Rating(idea), 2),
                        skeptic = idea.SkepticJson,
                        research = report,
                        appeal = idea.AppealJson,
                        notes = idea.NotesJson,
                        partner = idea.PartnerJson,
                        related = idea.RelatedJson,
                    }, LlmJson.Options);
                }

                case "find_ideas":
                {
                    var text = (read.Text ?? string.Empty).Trim();
                    if (text.Length < 2)
                    {
                        return "find_ideas: give text";
                    }

                    var pattern = $"%{text}%";
                    var hits = await db.Ideas
                        .Where(i => EF.Functions.ILike(i.Title, pattern) || EF.Functions.ILike(i.Thesis, pattern))
                        .OrderByDescending(i => i.Id)
                        .Take(12)
                        .Select(i => new { i.Id, i.Title, i.Status })
                        .ToListAsync(cancellationToken);
                    return "find_ideas: " + JsonSerializer.Serialize(hits, LlmJson.Options);
                }

                case "portfolio_stats":
                {
                    var byStatus = await db.Ideas.GroupBy(i => i.Status)
                        .Select(g => new { Status = g.Key, Count = g.Count() })
                        .ToListAsync(cancellationToken);
                    var signals = await db.Signals.CountAsync(cancellationToken);
                    var reports = await db.ResearchReports.CountAsync(cancellationToken);
                    var spendMonth = await db.AiLedger
                        .Where(e => e.Day >= new DateOnly(timeProvider.GetUtcNow().Year, timeProvider.GetUtcNow().Month, 1))
                        .SumAsync(e => e.CostUsd, cancellationToken);
                    return "portfolio_stats: " + JsonSerializer.Serialize(
                        new { byStatus, signals, reports, spendMonthUsd = Math.Round(spendMonth, 2) }, LlmJson.Options);
                }

                case "list_signals":
                {
                    var limit = Math.Clamp(read.Limit ?? 15, 1, 30);
                    var rows = await db.Signals
                        .OrderByDescending(s => s.Id)
                        .Take(limit)
                        .Select(s => new { s.Id, s.Kind, s.Summary, s.Audience })
                        .ToListAsync(cancellationToken);
                    return "list_signals: " + JsonSerializer.Serialize(rows, LlmJson.Options);
                }

                case "list_models":
                {
                    var overrides = await db.AppState
                        .Where(s => s.Key.StartsWith("model.override."))
                        .Select(s => new { s.Key, s.Value })
                        .ToListAsync(cancellationToken);
                    return "list_models(overrides only; empty = configured defaults): "
                        + JsonSerializer.Serialize(overrides, LlmJson.Options);
                }

                default:
                    return $"unknown tool '{read.Tool}'";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Hand read tool {Tool} failed", read.Tool);
            return $"{read.Tool}: failed ({ex.GetType().Name})";
        }
    }

    private async Task<List<(string Role, string Content)>> LoadSessionAsync(CancellationToken cancellationToken)
    {
        var state = await db.AppState.FindAsync([SessionKey], cancellationToken);
        if (state is null)
        {
            return [];
        }

        // Sessions idle >6h start fresh - stale context misleads more than it helps.
        if (state.UpdatedAt < timeProvider.GetUtcNow().AddHours(-6))
        {
            return [];
        }

        var parsed = LlmJson.SafeDeserialize<List<SessionLine>>(state.Value) ?? [];
        return [.. parsed.Select(l => (l.Role ?? "operator", l.Content ?? string.Empty))];
    }

    private async Task SaveSessionAsync(
        List<(string Role, string Content)> history, CancellationToken cancellationToken)
    {
        var trimmed = history.TakeLast(20)
            .Select(h => new SessionLine(h.Role, TextClip.Clip(h.Content, 1500)))
            .ToList();
        var json = JsonSerializer.Serialize(trimmed, LlmJson.Options);
        var state = await db.AppState.FindAsync([SessionKey], cancellationToken);
        if (state is null)
        {
            db.AppState.Add(new AppStateEntity
            {
                Key = SessionKey,
                Value = json,
                UpdatedAt = timeProvider.GetUtcNow(),
            });
        }
        else
        {
            state.Value = json;
            state.UpdatedAt = timeProvider.GetUtcNow();
        }
    }

    private static string BuildTranscript(List<(string Role, string Content)> history)
    {
        var builder = new StringBuilder();
        foreach (var (role, content) in history)
        {
            builder.Append(role).Append(": ").Append(content).Append('\n');
        }

        return builder.ToString();
    }

    private decimal RecordLedger(ChatCompletion? completion, HandOptions options)
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

    private sealed record HandReplyDto(
        [property: JsonPropertyName("say")] string? Say,
        [property: JsonPropertyName("reads")] List<HandRead>? Reads,
        [property: JsonPropertyName("writes")] List<HandWrite>? Writes);

    private sealed record SessionLine(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")] string? Content);
}

/// <summary>One read intent from the brain; executed by code, result fed back.</summary>
public sealed record HandRead(
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("filter")] string? Filter,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("limit")] int? Limit);
