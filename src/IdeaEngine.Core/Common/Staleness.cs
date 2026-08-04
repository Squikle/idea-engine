namespace IdeaEngine.Core.Common;

/// <summary>
/// An idea's research is STALE (\u231b) when new arguments or new reasoning exist that the
/// latest report never digested: engine milestones shipped since, or the operator
/// noted/appealed after it. Stale means "a /research re-run would actually change things".
/// </summary>
public static class Staleness
{
    public static bool IsStale(
        string? reportEngineVersion,
        DateTimeOffset reportCreatedAt,
        DateTimeOffset? lastNoteAt,
        DateTimeOffset? lastAppealAt)
    {
        if (ReasoningMilestones.MissedSince(reportEngineVersion).Count > 0)
        {
            return true;
        }

        if (lastNoteAt is { } note && note > reportCreatedAt)
        {
            return true;
        }

        return lastAppealAt is { } appeal && appeal > reportCreatedAt;
    }
}
