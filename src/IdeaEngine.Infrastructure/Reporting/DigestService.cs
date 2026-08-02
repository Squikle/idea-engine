using System.Globalization;
using System.Net;
using System.Text;
using IdeaEngine.Core.Common;
using IdeaEngine.Core.Pipeline;
using IdeaEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdeaEngine.Infrastructure.Reporting;

/// <summary>
/// The 21:00 daily digest: what arrived, what it means, what advanced, what it cost.
/// Honest by design - when nothing cleared the bar, it says so instead of inflating.
/// </summary>
public sealed class DigestService(
    IdeaEngineDbContext db,
    TimeProvider timeProvider,
    TimeZoneInfo timeZone,
    ILogger<DigestService> logger)
{
    public async Task<string> BuildAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var since = now.AddHours(-24);

        var itemsBySource = await db.RawItems
            .Where(r => r.FetchedAt >= since)
            .GroupBy(r => r.Source)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var signals = await db.Signals
            .Where(s => s.CreatedAt >= since)
            .Select(s => new
            {
                s.Kind,
                s.Summary,
                s.Glance,
                s.CommercialSentiment,
                s.Confidence,
                s.Novelty,
                s.RawItem!.Url,
                s.RawItem.Source,
            })
            .ToListAsync(cancellationToken);

        var ideas = await db.Ideas
            .Where(i => i.CreatedAt >= since && i.Category != "meta")
            .ToListAsync(cancellationToken);

        var research = await db.ResearchReports
            .Where(r => r.CreatedAt >= since)
            .Include(r => r.Idea)
            .ToListAsync(cancellationToken);

        var spend = await db.AiLedger
            .Where(e => e.CreatedAt >= since)
            .GroupBy(e => e.Stage)
            .Select(g => new { Stage = g.Key, Cost = g.Sum(e => e.CostUsd) })
            .ToListAsync(cancellationToken);

        logger.LogInformation(
            "Digest: {Items} items, {Signals} signals, {Ideas} ideas, {Reports} reports in 24h",
            itemsBySource.Sum(x => x.Count), signals.Count, ideas.Count, research.Count);

        var builder = new StringBuilder();
        builder.Append("<b>").Append(Ui.Digest).Append(" Daily digest · ")
            .Append(TimeZoneInfo.ConvertTime(now, timeZone).ToString("dd MMM", CultureInfo.InvariantCulture))
            .Append(' ').Append(Scheduling.ZoneLabel(timeZone)).Append("</b>\n");

        // Collection line.
        if (itemsBySource.Count > 0)
        {
            builder.Append(Ui.Collect).Append(" Collected: ")
                .AppendJoin(" · ", itemsBySource
                    .OrderByDescending(x => x.Count)
                    .Select(x => $"{x.Source} {x.Count}"))
                .Append('\n');
        }
        else
        {
            builder.Append(Ui.Collect).Append(" Collected: nothing in 24h (sources down? /status)\n");
        }

        builder.Append(Ui.Signal).Append(" Signals: +").Append(signals.Count).Append('\n');

        // Top signals by value.
        var top = signals
            .Select(s => new
            {
                s.Kind,
                Line = s.Glance ?? (s.Summary.Length > 90 ? s.Summary[..89] + "…" : s.Summary),
                s.Url,
                s.Source,
                Value = SignalScoring.Value(s.Confidence, s.Novelty, s.CommercialSentiment),
            })
            .OrderByDescending(s => s.Value)
            .Take(3)
            .ToList();

        if (top.Count > 0)
        {
            builder.Append("\n<b>🏆 Worth a look</b>\n");
            foreach (var signal in top)
            {
                builder.Append("• v").Append(signal.Value.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(' ').Append(WebUtility.HtmlEncode(signal.Line));
                if (signal.Url is { Length: > 0 })
                {
                    builder.Append(" <a href=\"").Append(signal.Url).Append("\">[").Append(signal.Source).Append("]</a>");
                }

                builder.Append('\n');
            }
        }

        // Ideas born today.
        var live = ideas.Where(i => i.Status is "candidate" or "validated" or "hot").ToList();
        var killed = ideas.Count - live.Count;
        builder.Append("\n<b>💡 Ideas</b>: ").Append(live.Count).Append(" 🟢 · ")
            .Append(killed).Append(" ☠️\n");
        foreach (var idea in live.OrderByDescending(i => i.Id).Take(4))
        {
            builder.Append("• ").Append(Ui.IdeaStatus(idea.Status)).Append(" #").Append(idea.Id).Append(' ')
                .Append(WebUtility.HtmlEncode(idea.Title.Length > 70 ? idea.Title[..69] + "…" : idea.Title))
                .Append('\n');
        }

        // Research verdicts.
        if (research.Count > 0)
        {
            builder.Append("\n<b>🔎 Researched</b>\n");
            foreach (var report in research.OrderByDescending(r => r.Id).Take(4))
            {
                builder.Append("• ").Append(Ui.Verdict(report.Verdict))
                    .Append(" #").Append(report.IdeaId).Append(' ')
                    .Append(WebUtility.HtmlEncode(
                        report.Idea is { Title: { } t } ? (t.Length > 60 ? t[..59] + "…" : t) : "?"))
                    .Append('\n');
            }
        }
        else if (live.Count == 0 && killed == 0)
        {
            builder.Append("Nothing cleared the bar today — honesty over noise.\n");
        }

        // Spend footer.
        var totalSpend = spend.Sum(s => s.Cost);
        builder.Append('\n').Append(Ui.Spend).Append(" Spend 24h: $").Append(totalSpend.ToString("F3", CultureInfo.InvariantCulture));
        if (spend.Count > 0)
        {
            builder.Append(" (")
                .AppendJoin(", ", spend.OrderByDescending(s => s.Cost)
                    .Select(s => $"{s.Stage} ${s.Cost.ToString("F3", CultureInfo.InvariantCulture)}"))
                .Append(')');
        }

        return builder.ToString().TrimEnd();
    }
}
