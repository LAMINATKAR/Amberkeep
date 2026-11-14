using System.Globalization;

namespace Amberkeep.Storage.Config;

/// <summary>When scheduled runs are due. Times are local; daylight-saving changes are handled.</summary>
public static class ScheduleCalculator
{
    /// <summary>The first scheduled time strictly after <paramref name="after"/>; null for <see cref="ScheduleKind.None"/>.</summary>
    public static DateTimeOffset? NextOccurrence(ScheduleConfig schedule, DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(zone);

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        switch (schedule.Kind)
        {
            case ScheduleKind.Interval:
                return after + TimeSpan.FromHours(Math.Max(1, schedule.IntervalHours));
            case ScheduleKind.Daily:
            case ScheduleKind.Weekly:
                // Within 8 days every chosen weekday has come round once after today.
                for (var i = 0; i < 8; i++)
                {
                    var day = today.AddDays(i);
                    if (schedule.Kind == ScheduleKind.Weekly && !schedule.DaysOfWeek.Contains(day.DayOfWeek))
                    {
                        continue;
                    }

                    if (At(day, schedule.Time, zone) is var candidate && candidate > after)
                    {
                        return candidate;
                    }
                }

                return null;
            case ScheduleKind.Monthly when schedule.DayOfMonth is >= 1 and <= 31:
                // This month's day may have passed; next month's has not.
                var month = new DateOnly(today.Year, today.Month, 1);
                for (var i = 0; i < 2; i++, month = month.AddMonths(1))
                {
                    var day = month.AddDays(Math.Min(schedule.DayOfMonth, DateTime.DaysInMonth(month.Year, month.Month)) - 1);
                    if (At(day, schedule.Time, zone) is var candidate && candidate > after)
                    {
                        return candidate;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// True when a scheduled run is due: the next occurrence after the last run
    /// (or after <paramref name="since"/> when there was none) is not in the future.
    /// Missed occurrences (computer off) are therefore caught up once.
    /// </summary>
    public static bool IsDue(ScheduleConfig schedule, DateTimeOffset? lastRun, DateTimeOffset since, DateTimeOffset now, TimeZoneInfo zone)
    {
        // An unknown or very old start (e.g. a hand-edited file without createdUtc) just means "missed".
        // A last run "in the future" (the clock was wrong once) says nothing: count from the start instead.
        if (lastRun > now)
        {
            lastRun = null;
        }

        var from = lastRun ?? since;
        var earliest = now.AddDays(-366);
        if (from < earliest)
        {
            from = earliest;
        }

        return NextOccurrence(schedule, from, zone) is { } next && next <= now;
    }

    /// <summary>A short description, e.g. "Daily 20:00", "Mon, Thu 20:00", "Monthly on the 15th, 20:00".</summary>
    public static string Describe(ScheduleConfig schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var time = schedule.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        return schedule.Kind switch
        {
            ScheduleKind.Daily => $"Daily {time}",
            ScheduleKind.Weekly => $"{DescribeDays(schedule.DaysOfWeek)} {time}",
            ScheduleKind.Monthly when schedule.DayOfMonth >= 31 => $"Monthly on the last day, {time}",
            ScheduleKind.Monthly => $"Monthly on the {Ordinal(schedule.DayOfMonth)}, {time}",
            ScheduleKind.Interval => string.Create(CultureInfo.InvariantCulture, $"Every {schedule.IntervalHours} h"),
            _ => "No schedule",
        };
    }

    /// <summary>"1st", "2nd", "3rd", "4th", …, "11th", "21st", "31st".</summary>
    public static string Ordinal(int day)
    {
        var suffix = (day % 100) is 11 or 12 or 13 ? "th" : (day % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
        return day.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    private static string DescribeDays(IReadOnlyList<DayOfWeek> days)
    {
        var set = days.Distinct().ToHashSet();
        if (set.Count == 7)
        {
            return "Daily";
        }

        DayOfWeek[] weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
        if (set.SetEquals(weekdays))
        {
            return "Weekdays";
        }

        if (set.SetEquals([DayOfWeek.Saturday, DayOfWeek.Sunday]))
        {
            return "Weekends";
        }

        var ordered = set.OrderBy(ScheduleConfig.MondayFirst).ToList();
        return ordered.Count == 1
            ? ordered[0] + "s"
            : string.Join(", ", ordered.Select(d => d.ToString()[..3]));
    }

    private static DateTimeOffset At(DateOnly day, TimeOnly time, TimeZoneInfo zone) => ToOffset(day.ToDateTime(time), zone);

    // A local time that does not exist (skipped by daylight saving) moves forward past the gap;
    // an ambiguous one (repeated hour) uses its first occurrence.
    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo zone)
    {
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
