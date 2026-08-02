namespace IdeaEngine.Core.Pipeline;

/// <summary>Everything the free heuristic stage knows about one researched idea.</summary>
public sealed record ReevalSnapshot(
    long IdeaId,
    string Title,
    int MissedUpgrades,
    int OpenQuestions,
    string Verdict,
    double UnifiedScore,
    bool NotesAfterResearch,
    bool AppealFlaggedShallow,
    bool HasRelations,
    double ReportAgeDays);

/// <summary>Stage-0 of the re-eval sweep: rank re-evaluation worthiness for $0.</summary>
public static class ReevalScoring
{
    /// <returns>Priority 0..1 plus human-readable reasons (shown to the owner and the screener).</returns>
    public static (double Priority, IReadOnlyList<string> Reasons) Score(ReevalSnapshot snapshot)
    {
        double priority = 0;
        var reasons = new List<string>();

        if (snapshot.MissedUpgrades > 0)
        {
            priority += Math.Min(snapshot.MissedUpgrades / 4.0, 1.0) * 0.45;
            reasons.Add($"missed {snapshot.MissedUpgrades} reasoning upgrade(s)");
        }

        if (snapshot.OpenQuestions > 0)
        {
            priority += Math.Min(snapshot.OpenQuestions / 3.0, 1.0) * 0.25;
            reasons.Add($"{snapshot.OpenQuestions} question(s) still open");
        }

        if (snapshot.Verdict == "no-go" && snapshot.UnifiedScore >= 0.45)
        {
            priority += 0.5;
            reasons.Add($"killed despite ⭐{snapshot.UnifiedScore * 100:F0}%");
        }
        else if (snapshot.Verdict == "maybe" && snapshot.UnifiedScore >= 0.6)
        {
            priority += 0.3;
            reasons.Add($"stuck uncertain at ⭐{snapshot.UnifiedScore * 100:F0}%");
        }

        if (snapshot.NotesAfterResearch)
        {
            priority += 0.35;
            reasons.Add("your notes arrived after the verdict");
        }

        if (snapshot.AppealFlaggedShallow)
        {
            priority += 0.4;
            reasons.Add("appeal called the judgment shallow/unfair");
        }

        if (snapshot.HasRelations && snapshot.ReportAgeDays > 14)
        {
            priority += 0.15;
            reasons.Add("new related ideas appeared since");
        }

        return (Math.Min(priority, 1.0), reasons);
    }

    /// <summary>Never-researched candidates: should they enter research under today's rules?</summary>
    public static (double Priority, IReadOnlyList<string> Reasons) ScoreBacklog(
        double estimate, double ageDays, string origin, bool hasRelations)
    {
        var priority = 0.2 + Math.Clamp(estimate, 0, 1) * 0.45;
        var reasons = new List<string> { $"never researched (≈{estimate * 100:F0}%)" };

        if (ageDays > 2)
        {
            priority += Math.Min(ageDays / 14.0, 1.0) * 0.2;
            reasons.Add($"waiting {ageDays:F0}d");
        }

        if (origin == "operator")
        {
            priority += 0.15;
            reasons.Add("your own drop");
        }
        else if (origin == "dig")
        {
            priority += 0.1;
            reasons.Add("dig spawn");
        }

        if (hasRelations)
        {
            priority += 0.1;
            reasons.Add("has related ideas");
        }

        return (Math.Min(priority, 1.0), reasons);
    }
}
