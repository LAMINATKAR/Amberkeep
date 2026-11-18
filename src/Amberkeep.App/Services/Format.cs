using System.Globalization;
using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Storage.Config;

namespace Amberkeep.App.Services;

/// <summary>Human-readable texts (English UI, invariant number format).</summary>
public static class Format
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(Culture, $"{bytes} B")
            : string.Create(Culture, $"{value:0.#} {units[unit]}");
    }

    /// <summary>"84.0 MB/s": always one decimal, so the text does not change width with every update.</summary>
    public static string Speed(double bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(Culture, $"{value:0.0} {units[unit]}");
    }

    /// <summary>"On connect · Daily 20:00", or null when the job only runs manually.</summary>
    public static string? Trigger(TriggerConfig trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return trigger.IsAutomatic
            ? string.Join(" · ", new[]
            {
                trigger.OnTargetConnected ? "On connect" : null,
                trigger.Schedule.Kind != ScheduleKind.None ? ScheduleCalculator.Describe(trigger.Schedule) : null,
            }.Where(s => s is not null))
            : null;
    }

    public static string Count(int count, string singular, string plural) =>
        string.Create(Culture, $"{count:N0} {(count == 1 ? singular : plural)}");

    public static string When(DateTimeOffset utc, DateTimeOffset now)
    {
        var local = utc.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today)
        {
            return string.Create(Culture, $"today {local:HH:mm}");
        }

        if (local.Date == today.AddDays(-1))
        {
            return string.Create(Culture, $"yesterday {local:HH:mm}");
        }

        return local.ToString("d MMM yyyy HH:mm", Culture);
    }

    public static string Duration(TimeSpan duration) => duration.TotalSeconds switch
    {
        < 1 => "less than a second",
        < 60 => string.Create(Culture, $"{duration.TotalSeconds:0} s"),
        < 3600 => string.Create(Culture, $"{(int)duration.TotalMinutes} min {duration.Seconds} s"),
        _ => string.Create(Culture, $"{(int)duration.TotalHours} h {duration.Minutes} min"),
    };

    /// <summary>"Only: Photos/2026, Documents +2 more" for a backup of selected folders and files; null for the whole folder.</summary>
    public static string? Selection(IReadOnlyList<string> includedPaths, int shown = 3) => includedPaths.Count switch
    {
        0 => null,
        _ when includedPaths.Count <= shown => "Only: " + string.Join(", ", includedPaths),
        _ => $"Only: {string.Join(", ", includedPaths.Take(shown))} +{includedPaths.Count - shown:N0} more",
    };

    /// <summary>The path of a recorded change; a moved file also shows where it was.</summary>
    public static string ChangePath(Amberkeep.Storage.History.RecordedChange change) =>
        change.From is { } from ? $"{change.Path}  (from {from})" : change.Path;

    public static string Category(DiffCategory category) => category switch
    {
        DiffCategory.New => "New",
        DiffCategory.Modified => "Modified",
        DiffCategory.TargetNewer => "Target newer",
        DiffCategory.TimestampOnly => "Timestamp",
        DiffCategory.CaseRename => "Renamed",
        DiffCategory.Moved => "Moved",
        DiffCategory.Removed => "Removed",
        DiffCategory.Error => "Error",
        _ => category.ToString(),
    };

    public static string Outcome(ExecutionOutcome outcome) => outcome switch
    {
        ExecutionOutcome.Completed => "Completed",
        ExecutionOutcome.CompletedWithErrors => "Completed with errors",
        ExecutionOutcome.Cancelled => "Cancelled",
        ExecutionOutcome.Refused => "Not started",
        _ => outcome.ToString(),
    };
}
