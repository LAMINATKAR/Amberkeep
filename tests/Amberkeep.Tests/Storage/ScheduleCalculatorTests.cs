using Amberkeep.Storage.Config;

namespace Amberkeep.Tests.Storage;

public class ScheduleCalculatorTests
{
    // A fixed zone with daylight saving (last Sunday of March / October, like Central Europe).
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Europe",
        TimeSpan.FromHours(1),
        "Test Europe",
        "Test Europe",
        "Test Europe Summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

    private static readonly ScheduleConfig Daily20 = new() { Kind = ScheduleKind.Daily, Time = new TimeOnly(20, 0) };

    [Fact]
    public void Daily_schedule_runs_later_today_or_tomorrow()
    {
        var morning = Local(2026, 11, 10, 9, 0);
        var evening = Local(2026, 11, 10, 21, 0);

        Assert.Equal(Local(2026, 11, 10, 20, 0), ScheduleCalculator.NextOccurrence(Daily20, morning, Zone));
        Assert.Equal(Local(2026, 11, 11, 20, 0), ScheduleCalculator.NextOccurrence(Daily20, evening, Zone));
        Assert.Equal(Local(2026, 11, 11, 20, 0), ScheduleCalculator.NextOccurrence(Daily20, Local(2026, 11, 10, 20, 0), Zone));
    }

    [Fact]
    public void Weekly_schedule_runs_on_the_given_day()
    {
        var weekly = new ScheduleConfig { Kind = ScheduleKind.Weekly, DayOfWeek = DayOfWeek.Friday, Time = new TimeOnly(18, 30) };

        // 10 Nov 2026 is a Tuesday.
        Assert.Equal(Local(2026, 11, 13, 18, 30), ScheduleCalculator.NextOccurrence(weekly, Local(2026, 11, 10, 9, 0), Zone));
        Assert.Equal(Local(2026, 11, 20, 18, 30), ScheduleCalculator.NextOccurrence(weekly, Local(2026, 11, 13, 19, 0), Zone));
    }

    [Fact]
    public void Interval_schedule_counts_from_the_last_run()
    {
        var interval = new ScheduleConfig { Kind = ScheduleKind.Interval, IntervalHours = 6 };
        var last = Local(2026, 11, 10, 9, 0);

        Assert.Equal(last.AddHours(6), ScheduleCalculator.NextOccurrence(interval, last, Zone));
    }

    [Fact]
    public void No_schedule_never_runs()
    {
        Assert.Null(ScheduleCalculator.NextOccurrence(new ScheduleConfig(), DateTimeOffset.Now, Zone));
        Assert.False(ScheduleCalculator.IsDue(new ScheduleConfig(), null, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, Zone));
    }

    [Fact]
    public void Missed_run_is_due_and_a_recent_run_moves_the_next_one()
    {
        var created = Local(2026, 11, 1, 10, 0);
        var now = Local(2026, 11, 10, 9, 0);

        Assert.True(ScheduleCalculator.IsDue(Daily20, lastRun: Local(2026, 11, 5, 20, 5), created, now, Zone));
        Assert.False(ScheduleCalculator.IsDue(Daily20, lastRun: Local(2026, 11, 9, 20, 5), created, now, Zone));
        Assert.False(ScheduleCalculator.IsDue(Daily20, lastRun: Local(2026, 11, 9, 23, 0), created, now, Zone));
        Assert.True(ScheduleCalculator.IsDue(Daily20, lastRun: null, created, now, Zone));
    }

    [Fact]
    public void Time_skipped_by_daylight_saving_moves_past_the_gap()
    {
        var at0230 = new ScheduleConfig { Kind = ScheduleKind.Daily, Time = new TimeOnly(2, 30) };

        // 29 Mar 2026: 02:00 jumps to 03:00.
        var next = ScheduleCalculator.NextOccurrence(at0230, Local(2026, 3, 28, 12, 0), Zone)!.Value;

        Assert.Equal(new DateTime(2026, 3, 29), next.Date);
        Assert.Equal(TimeSpan.FromHours(2), next.Offset);
        Assert.True(next.Hour >= 3);
    }

    [Fact]
    public void Repeated_hour_runs_once_at_its_first_occurrence()
    {
        var at0230 = new ScheduleConfig { Kind = ScheduleKind.Daily, Time = new TimeOnly(2, 30) };

        // 25 Oct 2026: 03:00 goes back to 02:00, so 02:30 happens twice.
        var first = ScheduleCalculator.NextOccurrence(at0230, Local(2026, 10, 24, 12, 0), Zone)!.Value;
        var after = ScheduleCalculator.NextOccurrence(at0230, first, Zone)!.Value;

        Assert.Equal(TimeSpan.FromHours(2), first.Offset);
        Assert.Equal(new DateTime(2026, 10, 26), after.Date);
    }

    [Fact]
    public void Description_is_short()
    {
        Assert.Equal("Daily 20:00", ScheduleCalculator.Describe(Daily20));
        Assert.Equal("Every 6 h", ScheduleCalculator.Describe(new ScheduleConfig { Kind = ScheduleKind.Interval }));
    }

    private static DateTimeOffset Local(int y, int m, int d, int h, int min)
    {
        var local = new DateTime(y, m, d, h, min, 0);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }
}
