namespace IdeaEngine.Core.Common;

/// <summary>Tiny scheduling helpers for daily fixed-time jobs.</summary>
public static class Scheduling
{
    /// <summary>Next occurrence of a UTC time-of-day strictly after <paramref name="nowUtc"/>.</summary>
    public static DateTimeOffset NextOccurrenceUtc(TimeOnly timeOfDayUtc, DateTimeOffset nowUtc)
    {
        var candidate = new DateTimeOffset(
            DateOnly.FromDateTime(nowUtc.UtcDateTime).ToDateTime(timeOfDayUtc), TimeSpan.Zero);

        return candidate > nowUtc ? candidate : candidate.AddDays(1);
    }

    /// <summary>
    /// Next UTC instant at which the wall clock in <paramref name="timeZone"/> shows
    /// <paramref name="localTime"/>. DST-safe: invalid local times (spring-forward gap)
    /// shift one hour later; ambiguous times use the standard-time offset.
    /// </summary>
    public static DateTimeOffset NextOccurrence(
        TimeOnly localTime, TimeZoneInfo timeZone, DateTimeOffset nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var date = DateOnly.FromDateTime(localNow.DateTime);

        for (var day = 0; day < 3; day++)
        {
            var localCandidate = DateTime.SpecifyKind(
                date.AddDays(day).ToDateTime(localTime), DateTimeKind.Unspecified);

            if (timeZone.IsInvalidTime(localCandidate))
            {
                localCandidate = localCandidate.AddHours(1);
            }

            var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localCandidate, timeZone));
            if (utc > nowUtc)
            {
                return utc;
            }
        }

        // Unreachable for sane inputs; keeps the compiler honest.
        return nowUtc.AddDays(1);
    }

    /// <summary>Short human label for a zone: "Toronto" for America/Toronto, "UTC" for UTC.</summary>
    public static string ZoneLabel(TimeZoneInfo timeZone) =>
        timeZone.Id == TimeZoneInfo.Utc.Id
            ? "UTC"
            : timeZone.Id[(timeZone.Id.LastIndexOf('/') + 1)..].Replace('_', ' ');
}
