using System.Globalization;
using System.Text.RegularExpressions;
using Amberkeep.Core.FileSystem;

namespace Amberkeep.Core.Execution;

/// <summary>Archive layout and retention: <c>&lt;target&gt;/.amberkeep/archive/&lt;yyyy-MM-dd_HHmmss&gt;[-n]/</c>.</summary>
public static partial class ArchiveCleaner
{
    public const string SnapshotFormat = "yyyy-MM-dd_HHmmss";

    /// <summary>Snapshot folder name for a run started at <paramref name="utcNow"/> (in local time, as users read it).</summary>
    public static string SnapshotName(DateTimeOffset utcNow) =>
        utcNow.ToLocalTime().ToString(SnapshotFormat, CultureInfo.InvariantCulture);

    /// <summary>Parses a snapshot folder name; null for folders that are not snapshots.</summary>
    public static DateTimeOffset? ParseSnapshotTime(string folderName)
    {
        var match = SnapshotPattern().Match(folderName);
        if (!match.Success)
        {
            return null;
        }

        return DateTime.TryParseExact(
            match.Groups["stamp"].Value,
            SnapshotFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var local)
            ? new DateTimeOffset(local)
            : null;
    }

    /// <summary>Deletes snapshots older than <paramref name="cutoff"/>. Folders with other names are left alone.</summary>
    /// <returns>Names of the deleted snapshots.</returns>
    internal static List<string> Purge(
        IWritableFileSystem fileSystem,
        WriteGuard guard,
        string archiveRoot,
        DateTimeOffset cutoff,
        string? keep)
    {
        var deleted = new List<string>();
        if (!fileSystem.DirectoryExists(archiveRoot))
        {
            return deleted;
        }

        foreach (var entry in fileSystem.ListDirectory(archiveRoot))
        {
            if (entry.Kind != EntryKind.Directory || entry.IsLink || entry.Name == keep)
            {
                continue;
            }

            var time = ParseSnapshotTime(entry.Name);
            if (time is not null && time < cutoff)
            {
                fileSystem.DeleteDirectoryTree(guard.Check(Path.Join(archiveRoot, entry.Name)));
                deleted.Add(entry.Name);
            }
        }

        return deleted;
    }

    [GeneratedRegex(@"^(?<stamp>\d{4}-\d{2}-\d{2}_\d{6})(-\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotPattern();
}
