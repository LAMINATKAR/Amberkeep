using System.Globalization;
using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;

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

    public static string Category(DiffCategory category) => category switch
    {
        DiffCategory.New => "New",
        DiffCategory.Modified => "Modified",
        DiffCategory.TargetNewer => "Target newer",
        DiffCategory.TimestampOnly => "Timestamp",
        DiffCategory.CaseRename => "Renamed",
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
