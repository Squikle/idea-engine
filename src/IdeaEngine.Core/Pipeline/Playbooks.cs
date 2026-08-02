namespace IdeaEngine.Core.Pipeline;

/// <summary>One strategic lens the pipeline applies when generating and judging ideas.</summary>
public sealed record Playbook(string Key, string Emoji, string Title, string Guidance);

/// <summary>
/// Operator wisdom turned into system behavior. Ideation rotates through these lenses
/// (or forces one via /ideate N key), the advocate uses them to find pivots, and the
/// skeptic/judge apply their fairness rules. Keys are single lowercase words for parsing.
/// </summary>
public static class Playbooks
{
    public static readonly IReadOnlyList<Playbook> All =
    [
        new("psych", "💰", "Psychology triggers",
            "Prioritize niches wired into standard human psychology: wealth/status (casino mechanics), beauty, education-as-investment, FOMO, identity purchases. People pay reliably where these triggers fire."),
        new("absurd", "🃏", "Absurd plays",
            "Joke-looking plays with real mechanics: I-Am-Rich status apps, Million-Dollar-Homepage scarcity grids. Judge on status signaling, scarcity and hype potential, not on utility."),
        new("nostalgia", "📼", "Nostalgia revival",
            "Everything new is forgotten old: find what people miss and bring it back better. Pre-existing audience awareness makes marketing cheap; nostalgia alone buys the first try."),
        new("toolabuse", "🔧", "Tool abuse",
            "Creative misuse of existing hardware/software as product seeds: mic tap-side detection, WiFi motion sensing. Look for capabilities nobody intended and package them."),
        new("arbitrage", "🧳", "Arbitrage & reimagining",
            "Take PROVEN demand to a dimension the incumbent left empty: underserved platforms (Apple Watch, niche OS), other countries/languages (Ukraine and Canada first - direct distribution access), audiences the product ignores, or missing companions (watch app, widget, offline mode) for a successful product. An existing competitor does NOT mean saturation when your target dimension is uncaptured - value these higher, competition risk is structurally lower."),
        new("copycat", "🏷", "Copycat but better",
            "The GitLab play: find overpriced or poorly reviewed incumbents (check Trustpilot/reviews), clone the core cheaper/kinder/simpler. Mine competitor weaknesses and complaints for the wedge."),
        new("reputation", "⭐", "Reputation plays",
            "Projects built for virality and GitHub stars, not revenue: vibecode-in-a-day meme tools, fun demos. Success metric is being featured/shared - reputation converts later."),
        new("content", "🎬", "Content plays",
            "Recurring 'help me choose X' questions people google and still ask on forums = content demand. AI-video or written answer channels earn the audience; the audience is the asset."),
        new("gamify", "🎮", "Gamification",
            "Turn routines into games: chores, cleaning, habits with progression, streaks, loot. People pay to make boring life feel like play."),
        new("gamble", "🎲", "Gamblification",
            "People bet on anything: prediction markets, mundane-event wagers, poker-like mechanics on daily life. Record legal/jurisdiction nuance as information, never self-censor."),
        new("aiwave", "🤖", "AI capability wave",
            "Each new model release unlocks products impossible last month. Wrap existing smart models in new form factors: agents controlling a PC (OpenClaw pattern), always-on background right-hand assistants, model X applied to niche Y. Model releases and rising AI startups are TIMING signals - being early to a capability beats being clever; collaboration with a hot new project can be the product."),
        new("datavalue", "🧲", "Data-value plays",
            "Products free or fun for users where usage itself produces a valuable byproduct: reCAPTCHA labeled datasets, Pokemon Go drove foot traffic. Design the free labor loop first, monetize the byproduct (data, attention, distribution) second."),
        new("pain", "🩹", "Pain buckets",
            "Chronic personal pains: procrastination, ADHD focus, waking up, toxic teammates. Cross-reference known techniques (pomodoro, meditation) with what sufferers say still fails - build for the unsolved residue."),
    ];

    private static readonly Dictionary<string, Playbook> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string? key, out Playbook playbook)
    {
        if (key is not null && ByKey.TryGetValue(key.Trim(), out var found))
        {
            playbook = found;
            return true;
        }

        playbook = All[0];
        return false;
    }

    /// <summary>Random distinct lenses for a rotation session.</summary>
    public static IReadOnlyList<Playbook> Sample(int count)
    {
        var indices = Enumerable.Range(0, All.Count).ToArray();
        Random.Shared.Shuffle(indices);
        return [.. indices.Take(Math.Clamp(count, 1, All.Count)).Select(i => All[i])];
    }

    /// <summary>Compact one-line-per-lens list for prompts (~300 tokens).</summary>
    public static string CompactList() =>
        string.Join('\n', All.Select(p => $"- {p.Key}: {p.Title} — {p.Guidance}"));
}
