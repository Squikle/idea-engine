using System.Text.RegularExpressions;
using IdeaEngine.Infrastructure.Persistence.Entities;

namespace IdeaEngine.Infrastructure.Triage;

/// <summary>
/// Free heuristic gate before any tokens are spent. Deliberately mild: the nano-model
/// verdict is cheap, so only obvious junk dies here. Tighten only with evidence.
/// </summary>
public static partial class Prefilter
{
    public static bool ShouldAnalyze(RawItemEntity item, out string? reason)
    {
        if (item.Title.Length < 15)
        {
            reason = "title too short";
            return false;
        }

        if (RecurringThreadPattern().IsMatch(item.Title))
        {
            reason = "recurring housekeeping thread";
            return false;
        }

        reason = null;
        return true;
    }

    [GeneratedRegex(
        @"\b(megathread|daily (thread|discussion)|weekly (thread|discussion)|monthly (thread|discussion)|giveaway|open thread|free talk)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RecurringThreadPattern();
}
