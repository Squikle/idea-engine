namespace IdeaEngine.Core.Common;

/// <summary>One reasoning-relevant upgrade of the pipeline (minor releases only).</summary>
public sealed record ReasoningMilestone(Version Version, string Summary);

/// <summary>
/// The reasoning changelog: which engine upgrades actually changed HOW verdicts are made.
/// Used by the re-eval sweep to spot ideas judged by an older, weaker brain.
/// Append-only; cosmetic releases don't belong here.
/// </summary>
public static class ReasoningMilestones
{
    public static readonly IReadOnlyList<ReasoningMilestone> All =
    [
        new(new Version(0, 7, 0), "web research with grounded verdicts introduced"),
        new(new Version(0, 11, 0), "multi-round closure research + full page reading"),
        new(new Version(0, 13, 0), "unified category scoring (demand/pay/build/gap)"),
        new(new Version(0, 17, 0), "advocate-vs-skeptic debate, operator notes, court of appeal"),
        new(new Version(0, 18, 0), "playbook lenses + arbitrage valuation (empty dimension != saturated)"),
        new(new Version(0, 20, 0), "individual judgment (no tournament), idea relations, archive corpus"),
        new(new Version(0, 25, 1), "API/MCP-first upstream lens (shovel-seller variants in judging)"),
        new(new Version(0, 27, 0), "pre-research skeptic kills auto-appealed on operator drops; overturn revives to candidate"),
    ];

    /// <summary>Upgrades a report produced at <paramref name="reportVersion"/> has never seen.
    /// Null/unparseable = pre-versioning era = missed everything.</summary>
    public static IReadOnlyList<ReasoningMilestone> MissedSince(string? reportVersion)
    {
        if (!Version.TryParse(reportVersion, out var version))
        {
            return All;
        }

        return [.. All.Where(m => m.Version > version)];
    }
}
