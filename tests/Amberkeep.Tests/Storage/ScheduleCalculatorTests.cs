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
        var weekly = new ScheduleConfig { Kind = ScheduleKind.Weekly, DaysOfWeek = [DayOfWeek.Friday], Time = new TimeOnly(18, 30) };

        // 10 Nov 2026 is a Tuesday.
        Assert.Equal(Local(2026, 11, 13, 18, 30), ScheduleCalculator.NextOccurrence(weekly, Local(2026, 11, 10, 9, 0), Zone));
        Assert.Equal(Local(2026, 11, 20, 18, 30), ScheduleCalculator.NextOccurrence(weekly, Local(2026, 11, 13, 19, 0), Zone));
    }

    [Fact]
    public void Weekly_schedule_with_several_days_runs_on_the_nearest_one()
    {
        var monThu = new ScheduleConfig { Kind = ScheduleKind.Weekly, DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Thursday], Time = new TimeOnly(20, 0) };

        // Tue 10 Nov 2026 → Thu 12; Thu 12 after 20:00 → Mon 16; Sun 15 → Mon 16.
        Assert.Equal(Local(2026, 11, 12, 20, 0), ScheduleCalculator.NextOccurrence(monThu, Local(2026, 11, 10, 9, 0), Zone));
        Assert.Equal(Local(2026, 11, 16, 20, 0), ScheduleCalculator.NextOccurrence(monThu, Local(2026, 11, 12, 20, 0), Zone));
        Assert.Equal(Local(2026, 11, 16, 20, 0), ScheduleCalculator.NextOccurrence(monThu, Local(2026, 11, 15, 23, 0), Zone));
        Assert.Null(ScheduleCalculator.NextOccurrence(monThu with { DaysOfWeek = [] }, Local(2026, 11, 10, 9, 0), Zone));
    }

    [Fact]
    public void Monthly_schedule_runs_on_the_given_day()
    {
        var on15 = new ScheduleConfig { Kind = ScheduleKind.Monthly, DayOfMonth = 15, Time = new TimeOnly(20, 0) };

        Assert.Equal(Local(2026, 11, 15, 20, 0), ScheduleCalculator.NextOccurrence(on15, Local(2026, 11, 10, 9, 0), Zone));
        Assert.Equal(Local(2026, 12, 15, 20, 0), ScheduleCalculator.NextOccurrence(on15, Local(2026, 11, 15, 20, 0), Zone));
        Assert.Equal(Local(2027, 1, 15, 20, 0), ScheduleCalculator.NextOccurrence(on15, Local(2026, 12, 31, 23, 0), Zone));
    }

    [Theory]
    [InlineData(2027, 2, 28)] // February
    [InlineData(2028, 2, 29)] // leap year
    [InlineData(2027, 4, 30)] // 30-day month
    [InlineData(2027, 5, 31)]
    public void Monthly_day_31_runs_on_the_last_day_of_each_month(int year, int month, int lastDay)
    {
        var last = new ScheduleConfig { Kind = ScheduleKind.Monthly, DayOfMonth = 31, Time = new TimeOnly(20, 0) };

        var next = ScheduleCalculator.NextOccurrence(last, Local(year, month, 1, 0, 0), Zone);

        Assert.Equal(Local(year, month, lastDay, 20, 0), next);
    }

    [Fact]
    public void Monthly_day_30_in_february_runs_on_its_last_day_and_then_on_the_30th()
    {
        var on30 = new ScheduleConfig { Kind = ScheduleKind.Monthly, DayOfMonth = 30, Time = new TimeOnly(8, 0) };

        var feb = ScheduleCalculator.NextOccurrence(on30, Local(2027, 2, 10, 0, 0), Zone)!.Value;
        Assert.Equal(Local(2027, 2, 28, 8, 0), feb);
        Assert.Equal(Local(2027, 3, 30, 8, 0), ScheduleCalculator.NextOccurrence(on30, feb, Zone));
    }

    [Fact]
    public void Missed_monthly_run_is_due_once()
    {
        var on1 = new ScheduleConfig { Kind = ScheduleKind.Monthly, DayOfMonth = 1, Time = new TimeOnly(20, 0) };
        var created = Local(2026, 9, 20, 10, 0);

        // The computer was off on 1 Nov; on 5 Nov it is due, and after that run not again until 1 Dec.
        Assert.True(ScheduleCalculator.IsDue(on1, lastRun: Local(2026, 10, 1, 20, 1), created, Local(2026, 11, 5, 9, 0), Zone));
        Assert.False(ScheduleCalculator.IsDue(on1, lastRun: Local(2026, 11, 5, 9, 1), created, Local(2026, 11, 30, 9, 0), Zone));
        Assert.True(ScheduleCalculator.IsDue(on1, lastRun: Local(2026, 11, 5, 9, 1), created, Local(2026, 12, 1, 20, 0), Zone));
    }

    [Fact]
    public void Monthly_time_skipped_by_daylight_saving_moves_past_the_gap()
    {
        // 29 Mar 2026 02:30 does not exist.
        var on29 = new ScheduleConfig { Kind = ScheduleKind.Monthly, DayOfMonth = 29, Time = new TimeOnly(2, 30) };

        var next = ScheduleCalculator.NextOccurrence(on29, Local(2026, 3, 1, 0, 0), Zone)!.Value;

        Assert.Equal(new DateTime(2026, 3, 29), next.Date);
        Assert.True(next.Hour >= 3);
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
        var weekly = new ScheduleConfig { Kind = ScheduleKind.Weekly, Time = new TimeOnly(20, 0) };
        Assert.Equal("Fridays 20:00", ScheduleCalculator.Describe(weekly with { DaysOfWeek = [DayOfWeek.Friday] }));
        Assert.Equal("Mon, Thu 20:00", ScheduleCalculator.Describe(weekly with { DaysOfWeek = [DayOfWeek.Thursday, DayOfWeek.Monday] }));
        Assert.Equal("Weekdays 20:00", ScheduleCalculator.Describe(weekly with { DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday] }));
        Assert.Equal("Weekends 20:00", ScheduleCalculator.Describe(weekly with { DaysOfWeek = [DayOfWeek.Sunday, DayOfWeek.Saturday] }));
        Assert.Equal("Daily 20:00", ScheduleCalculator.Describe(weekly with { DaysOfWeek = Enum.GetValues<DayOfWeek>() }));
        var monthly = new ScheduleConfig { Kind = ScheduleKind.Monthly, Time = new TimeOnly(8, 5) };
        Assert.Equal("Monthly on the 1st, 08:05", ScheduleCalculator.Describe(monthly));
        Assert.Equal("Monthly on the 22nd, 08:05", ScheduleCalculator.Describe(monthly with { DayOfMonth = 22 }));
        Assert.Equal("Monthly on the last day, 08:05", ScheduleCalculator.Describe(monthly with { DayOfMonth = 31 }));
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(23, "23rd")]
    [InlineData(31, "31st")]
    public void Ordinals(int day, string expected) => Assert.Equal(expected, ScheduleCalculator.Ordinal(day));

    private static DateTimeOffset Local(int y, int m, int d, int h, int min)
    {
        var local = new DateTime(y, m, d, h, min, 0);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }
}
