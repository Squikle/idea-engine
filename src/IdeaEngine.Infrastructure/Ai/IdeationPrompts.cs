using System.Globalization;
using System.Text;

namespace IdeaEngine.Infrastructure.Ai;

/// <summary>A signal line offered to ideation models, with a citable id.</summary>
public sealed record GroundingSignal(
    long Id, string Kind, string CommercialSentiment, double Confidence,
    string Summary, string? Audience, string? Community);

/// <summary>Prompts for ideation sessions. Pure and testable; the rubric IS the product.</summary>
public static class IdeationPrompts
{
    public const string BuilderSystem =
        """
        You are a pragmatic indie product builder. From the provided market signals (each has an
        id like S123), propose exactly ONE product idea a solo developer/maker could realistically
        start. Reply with ONLY a JSON object:
        {"title":"...","thesis":"2-3 sentences: what it is and why now","category":"saas|app|website|3dprint|hardware|wearable|service|content","target_user":"...","effort":1-5,"monetization":"...","distribution_note":"realistic first channel; mention Ukraine/Canada angle only if genuine","cited_signals":["S12","S45"],"assumptions":["..."]}
        Rules:
        - Ground the idea in at least 2 cited signals; use their ids. Ideas without grounding are worthless.
        - Prefer boring-but-buyable over clever-but-vague. No crypto/blockchain.
        - effort: 1=weekend project, 3=months of evenings, 5=a real business build.
        """;

    public const string SkepticSystem =
        """
        You are a ruthless startup due-diligence skeptic. You receive an idea JSON plus the market
        signals it cites. Attack it. Reply with ONLY a JSON object:
        {"verdict":"advance|kill","kill_reasons":["..."],"weaknesses":["..."],"existing_solutions":["name or 'search: <query>' - why it competes"],"research_questions":["3-5 concrete questions web research must answer before anyone spends a weekend on this"],"scores":{"demand":0.0-1.0,"willingness_to_pay":0.0-1.0,"feasibility_solo":0.0-1.0,"differentiation":0.0-1.0},"confidence":0.0-1.0}
        Rules:
        - Most ideas deserve "kill"; advancing is the exception, never politeness.
        - Verify the cited signals actually support the idea; complaints without purchase intent are not demand.
        - Saturated market with no differentiation = kill. Unreachable audience = kill.
        - Agreement between AIs is not evidence: name what EXTERNAL research must verify.
        """;

    public const string MetaSystem =
        """
        You advise on improving an automated product-opportunity discovery pipeline. You receive
        its architecture summary and live stats. Propose what its operators likely overlooked.
        Reply with ONLY a JSON object:
        {"proposals":[{"kind":"source|prompt|pipeline|scoring|other","title":"...","what":"1-3 sentences","why":"expected benefit","verify":"cheapest way to check feasibility/legality/cost","effort":1-5}]}
        Rules:
        - 3 to 6 proposals, specific (names, endpoints, communities), not generic advice.
        - Sources must be legal to read: official APIs, RSS/Atom, open datasets. No ToS-violating scraping.
        - Consider: niche forums with feeds, non-English communities (Ukrainian, Canadian), open
          datasets, marketplaces with APIs, and weak stages (dedup, clustering, scoring, feedback loops).
        """;

    public static string BuildGrounding(IReadOnlyList<GroundingSignal> signals)
    {
        var builder = new StringBuilder("Market signals (cite by id):\n");
        foreach (var signal in signals)
        {
            builder.Append('S').Append(signal.Id.ToString(CultureInfo.InvariantCulture))
                .Append(" [").Append(signal.Kind).Append('/').Append(signal.CommercialSentiment)
                .Append(" c").Append(signal.Confidence.ToString("F2", CultureInfo.InvariantCulture))
                .Append("] ").Append(signal.Summary);

            if (signal.Audience is { Length: > 0 })
            {
                builder.Append(" (audience: ").Append(signal.Audience).Append(')');
            }

            if (signal.Community is { Length: > 0 })
            {
                builder.Append(" (from: ").Append(signal.Community).Append(')');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    public static string BuildSkepticMessage(string ideaJson, IReadOnlyList<GroundingSignal> citedSignals)
    {
        var builder = new StringBuilder("Idea under review:\n").Append(ideaJson).Append('\n');
        if (citedSignals.Count > 0)
        {
            builder.Append('\n').Append(BuildGrounding(citedSignals));
        }

        return builder.ToString();
    }
}
