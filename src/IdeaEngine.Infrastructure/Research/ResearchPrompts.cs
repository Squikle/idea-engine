using System.Globalization;
using System.Text;

namespace IdeaEngine.Infrastructure.Research;

/// <summary>Prompts for the web-research validation stage. Pure and testable.</summary>
public static class ResearchPrompts
{
    public const string PlanningSystem =
        """
        You plan web research to validate a product idea. Reply with ONLY a JSON object:
        {"queries":["..."]}
        Rules: 4-8 specific web search queries. Cover: direct competitors and existing products,
        pricing of alternatives, evidence of demand (forums, reviews, marketplaces), and the
        skeptic's open questions. Use concrete product nouns, not abstractions. English only.
        """;

    public const string SynthesisSystem =
        """
        You are a due-diligence researcher finishing a validation report. You receive an idea,
        the skeptic's open questions, and web search results (title, url, snippet). Ground EVERY
        claim in those results; when the results don't answer something, write "not found in
        results" - never invent. Reply with ONLY a JSON object:
        {"verdict":"go|maybe|no-go","confidence":0.0-1.0,
         "competitors":[{"name":"...","url":"...","why":"..."}],
         "answers":[{"question":"...","answer":"...","evidence_urls":["..."]}],
         "market_notes":"...","differentiation_path":"...","risks":["..."],
         "mvp_test":"the cheapest ~1-week experiment that tests the riskiest assumption",
         "related_variants":["adjacent niches/applications that looked stronger in the results"],
         "next_steps":["concrete first actions if pursued"],
         "scores":{"demand":0.0-1.0,"competition_gap":0.0-1.0,"willingness_to_pay":0.0-1.0,"feasibility_solo":0.0-1.0}}
        Rules:
        - "no-go" when a strong incumbent covers the need with no realistic differentiation.
        - "go" requires BOTH demand evidence AND a reachable gap; otherwise "maybe".
        - Garage-scale lens: launchable by 1-3 people from an apartment.
        - Do not moralize and do not kill for edginess; record legal nuance (what, where) in
          risks - the operator filters manually.
        - Cite urls ONLY from the provided results.
        """;

    public static string BuildIdeaContext(
        string title,
        string thesis,
        string category,
        string? targetUser,
        string? monetization,
        int effort,
        IReadOnlyList<string> openQuestions,
        IReadOnlyList<string> evidenceSummaries)
    {
        var builder = new StringBuilder();
        builder.Append("Idea: ").Append(title).Append('\n')
            .Append("Thesis: ").Append(thesis).Append('\n')
            .Append("Category: ").Append(category)
            .Append(" · effort ").Append(effort.ToString(CultureInfo.InvariantCulture)).Append('\n');

        if (!string.IsNullOrWhiteSpace(targetUser))
        {
            builder.Append("Target user: ").Append(targetUser).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(monetization))
        {
            builder.Append("Monetization: ").Append(monetization).Append('\n');
        }

        if (openQuestions.Count > 0)
        {
            builder.Append("Open questions from the skeptic:\n");
            foreach (var question in openQuestions)
            {
                builder.Append("? ").Append(question).Append('\n');
            }
        }

        if (evidenceSummaries.Count > 0)
        {
            builder.Append("Original demand signals:\n");
            foreach (var evidence in evidenceSummaries)
            {
                builder.Append("- ").Append(evidence).Append('\n');
            }
        }

        return builder.ToString();
    }

    public static string BuildSynthesisMessage(
        string ideaContext,
        IReadOnlyList<(string Query, IReadOnlyList<SearchHit> Hits)> searchBlocks,
        IReadOnlyList<(string Url, string Excerpt)>? pageExcerpts = null)
    {
        var builder = new StringBuilder(ideaContext).Append("\nWeb search results:\n");

        var index = 1;
        foreach (var (query, hits) in searchBlocks)
        {
            builder.Append("\n[Q").Append(index++).Append("] ").Append(query).Append('\n');
            if (hits.Count == 0)
            {
                builder.Append("(no results)\n");
                continue;
            }

            foreach (var hit in hits)
            {
                builder.Append("- ").Append(hit.Title).Append(" | ").Append(hit.Url)
                    .Append(" | ").Append(hit.Description).Append('\n');
            }
        }

        if (pageExcerpts is { Count: > 0 })
        {
            builder.Append("\nFull page excerpts (read for detail - pricing, features, positioning):\n");
            var pageIndex = 1;
            foreach (var (url, excerpt) in pageExcerpts)
            {
                builder.Append("\n[P").Append(pageIndex++).Append("] ").Append(url).Append('\n')
                    .Append(excerpt).Append('\n');
            }
        }

        return builder.ToString();
    }
}
