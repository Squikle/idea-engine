using IdeaEngine.Core.Common;

namespace IdeaEngine.Tests.Common;

public sealed class SchedulingTests
{
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    [Fact]
    public void NextOccurrenceUtc_TodayWhenStillAhead_TomorrowWhenPassed()
    {
        var now = new DateTimeOffset(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 2, 14, 0, 0, TimeSpan.Zero),
            Scheduling.NextOccurrenceUtc(new TimeOnly(14, 0), now));
        Assert.Equal(
            new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.Zero),
            Scheduling.NextOccurrenceUtc(new TimeOnly(8, 0), now));
    }

    [Fact]
    public void NextOccurrence_TorontoSummer_IsUtcMinus4()
    {
        // 2026-08-02 12:00 UTC = 08:00 in Toronto (EDT). Next 10:00 Toronto = 14:00 UTC.
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        var next = Scheduling.NextOccurrence(new TimeOnly(10, 0), Toronto, now);

        Assert.Equal(new DateTimeOffset(2026, 8, 2, 14, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextOccurrence_AlreadyPassedLocally_RollsToTomorrow()
    {
        // 2026-08-03 02:00 UTC = 22:00 Aug 2 Toronto; next 21:00 Toronto is Aug 3 -> 01:00 UTC Aug 4.
        var now = new DateTimeOffset(2026, 8, 3, 2, 0, 0, TimeSpan.Zero);

        var next = Scheduling.NextOccurrence(new TimeOnly(21, 0), Toronto, now);

        Assert.Equal(new DateTimeOffset(2026, 8, 4, 1, 0, 0, TimeSpan.Zero), next);
    }

    [Theory]
    [InlineData("America/Toronto", "Toronto")]
    [InlineData("America/New_York", "New York")]
    public void ZoneLabel_HumanReadable(string id, string expected)
    {
        Assert.Equal(expected, Scheduling.ZoneLabel(TimeZoneInfo.FindSystemTimeZoneById(id)));
    }

    [Fact]
    public void ZoneLabel_Utc()
    {
        Assert.Equal("UTC", Scheduling.ZoneLabel(TimeZoneInfo.Utc));
    }
}
