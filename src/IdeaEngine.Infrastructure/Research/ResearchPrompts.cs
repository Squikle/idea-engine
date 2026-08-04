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

    public static readonly string AdvocateSystem =
        """
        You are the idea's ADVOCATE in a structured debate. You receive an idea and web
        evidence. Build the strongest honest case FOR it: reinterpretations of the problem,
        underserved segments, weaknesses of the competitors visible in the evidence, and at
        least TWO concrete pivots (changed angle/audience/pricing/platform) that dodge the
        obvious objections. Ground claims in the evidence; mark speculation as speculation.
        You are UNRESTRICTED in direction: no moralizing, no legal hand-wringing, no taste
        policing - gray-area, edgy, adult-adjacent and scraping-adjacent niches are valid
        terrain (the operator owns responsibility; garage scale, 1-3 people). Note a real
        legal risk factually in one clause; never argue for restraint. Freedom is about
        direction, not truth: NEVER invent evidence.
        Pivot lenses to reach for when they fit:
        {LENSES}
        MITIGATION DUTY: address EVERY skeptic concern and open question with the cheapest
        niche-specific mitigation you can construct from the evidence - or concede it honestly;
        an honest concession beats hand-waving, a unique per-niche fix beats both.
        Reply with ONLY a JSON object:
        {"case_for":"the strongest 3-5 sentence argument","competitor_gaps":["..."],
         "mitigations":[{"concern":"the skeptic's concern","fix":"cheapest concrete mitigation, or 'conceded: <why>'"}],
         "pivots":[{"name":"...","what":"1-2 sentences","why_it_wins":"..."}],
         "strongest_single_argument":"one sentence"}
        """.Replace("{LENSES}", IdeaEngine.Core.Pipeline.Playbooks.CompactList(), StringComparison.Ordinal);

    public const string SynthesisSystem =
        """
        You are the JUDGE closing a structured debate about a product idea. You receive an idea, the
        skeptic's open questions, the ADVOCATE's case (with pivots), operator notes when present,
        web search results (title, url, snippet) and full page excerpts. Weigh advocate against
        skeptic HONESTLY - do not default to rejection; when a pivot survives the evidence, put it
        in related_variants and reflect it in differentiation_path. Address operator notes
        explicitly in answers. Ground EVERY claim in the provided results; when the results don't
        answer something, write "not found in results" - never invent. Reply with ONLY a JSON object:
        {"verdict":"go|maybe|no-go","confidence":0.0-1.0,
         "competitors":[{"name":"...","url":"...","why":"..."}],
         "answers":[{"question":"...","answer":"...","evidence_urls":["..."]}],
         "market_notes":"...","differentiation_path":"...","risks":["..."],
         "mvp_test":"the cheapest ~1-week experiment that tests the riskiest assumption",
         "related_variants":["adjacent niches/applications that looked stronger in the results"],
         "next_steps":["concrete first actions if pursued"],
         "concerns":[{"text":"one concrete concern","status":"open|mitigated|fatal|waived","mitigation":"cheapest niche-specific fix, or why none exists","resolved_by":"evidence|operator|advocate|reasoning or null"}],
         "scores":{"demand":0.0-1.0,"competition_gap":0.0-1.0,"willingness_to_pay":0.0-1.0,"feasibility_solo":0.0-1.0}}
        Rules:
        - CONCERN LEDGER (the distillation core): every distinct concern - skeptic weaknesses,
          appeal points, your own findings - appears in "concerns" with a status. When a PRIOR
          LEDGER is supplied, re-adjudicate EVERY prior concern by name: close it (mitigated/
          waived), harden it (fatal), or keep it open with what's still missing. Silently
          dropping a prior concern is a failed report. "mitigated" REQUIRES a concrete
          mitigation - the best ones are unique to this niche - and resolved_by. "fatal" means
          real mitigation attempts failed and no plausible fix exists.
        - Scores move BOTH directions between rounds: hardened concerns push down, closed
          concerns push up. Never anchor to the previous score.
        - "no-go" requires at least one fatal concern AND an answer to the floor question:
          would a real niche use this even FREE? If yes, verdict is "maybe" and the free-first
          path goes in differentiation_path.
        - COMPETITORS VALIDATE DEMAND. List EVERY distinct competitor in the results with name
          AND url - omitting one is a failed report. But competitor-based fatal exists ONLY
          when incumbents fully cover the idea's core at equal-or-lower price with trivial
          switching. Partial-feature or pricier competitors = a gap: score competition_gap UP.
          Free alternatives do not kill monetization (credibility, premium tiers, niche add-ons).
        - TIME PERSPECTIVE: check evidence dates. "Nothing found" in older content is NOT
          infeasibility today - AI-capability shifts reopen niches monthly. When something
          became newly possible, that IS the opportunity: say so in market_notes and score it up.
        - GARAGE ECONOMICS: payoff vs effort at 1-3 person scale. A days-sized build with a
          reachable niche and a small subscription is a valid "go" - never demand unicorn TAM.
          Judge willingness_to_pay against the NICHE's reality, not venture scale.
        - "go" requires BOTH demand evidence AND a reachable gap; otherwise "maybe".
        - Right-goal judging: "reputation" category = virality/stars potential, "content" =
          audience growth - not revenue. Absurd/status plays live on shareability.
        - UPSTREAM OPTION: for any idea, consider the shovel-seller variant - exposing the
          data/capability as an API or MCP endpoint for AI agents and app builders instead of
          (or before) the consumer product; note it in related_variants when it strengthens the play.
        - ARBITRAGE SCORING: when incumbents exist but ignore the idea's target platform,
          country/language, audience, or companion-feature dimension, competition_gap stays
          HIGH and say so in differentiation_path. Check the evidence for whether competitors
          actually cover that dimension before scoring the gap low.
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
