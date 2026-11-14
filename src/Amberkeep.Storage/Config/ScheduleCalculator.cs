namespace Amberkeep.Storage.Config;

/// <summary>When scheduled runs are due. Times are local; daylight-saving changes are handled.</summary>
public static class ScheduleCalculator
{
    /// <summary>The first scheduled time strictly after <paramref name="after"/>; null for <see cref="ScheduleKind.None"/>.</summary>
    public static DateTimeOffset? NextOccurrence(ScheduleConfig schedule, DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(zone);

        switch (schedule.Kind)
        {
            case ScheduleKind.Interval:
                return after + TimeSpan.FromHours(Math.Max(1, schedule.IntervalHours));
            case ScheduleKind.Daily:
            case ScheduleKind.Weekly:
                var local = TimeZoneInfo.ConvertTime(after, zone);
                var date = DateOnly.FromDateTime(local.DateTime);
                for (var i = 0; i < 8; i++)
                {
                    var day = date.AddDays(i);
                    if (schedule.Kind == ScheduleKind.Weekly && day.DayOfWeek != schedule.DayOfWeek)
                    {
                        continue;
                    }

                    var candidate = ToOffset(day.ToDateTime(schedule.Time), zone);
                    if (candidate > after)
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
        var from = lastRun ?? since;
        var earliest = now.AddDays(-366);
        if (from < earliest)
        {
            from = earliest;
        }

        return NextOccurrence(schedule, from, zone) is { } next && next <= now;
    }

    /// <summary>A short description, e.g. "Daily 20:00".</summary>
    public static string Describe(ScheduleConfig schedule) => schedule.Kind switch
    {
        ScheduleKind.Daily => $"Daily {schedule.Time:HH\\:mm}",
        ScheduleKind.Weekly => $"{schedule.DayOfWeek}s {schedule.Time:HH\\:mm}",
        ScheduleKind.Interval => $"Every {schedule.IntervalHours} h",
        _ => "No schedule",
    };

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
