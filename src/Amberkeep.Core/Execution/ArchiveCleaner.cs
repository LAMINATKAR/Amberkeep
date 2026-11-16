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

    /// <summary>Archive folder of a target.</summary>
    public static string ArchiveRoot(string targetPath) =>
        Path.Join(targetPath, Scanning.Scanner.ProgramFolderName, "archive");

    /// <summary>Deletes snapshots in the archive of <paramref name="targetPath"/> older than <paramref name="cutoff"/>.</summary>
    /// <returns>Names of the deleted snapshots.</returns>
    /// <exception cref="IOException"><c>.amberkeep</c> or its archive is a link or junction.</exception>
    public static IReadOnlyList<string> PurgeSnapshots(IWritableFileSystem fileSystem, string targetPath, DateTimeOffset cutoff)
    {
        EnsureNotLinked(fileSystem, targetPath);
        return Purge(fileSystem, new WriteGuard(null, ArchiveRoot(targetPath)), ArchiveRoot(targetPath), cutoff, keep: null);
    }

    /// <summary>
    /// Refuses an archive reached through a link: with <c>.amberkeep</c> or <c>archive</c> linked to another folder,
    /// the snapshots of that folder would be listed, deleted or restored as if they were this target's.
    /// </summary>
    /// <exception cref="IOException">One of the two folders is a link or junction.</exception>
    public static void EnsureNotLinked(IWritableFileSystem fileSystem, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        foreach (var folder in new[] { Path.Join(targetPath, Scanning.Scanner.ProgramFolderName), ArchiveRoot(targetPath) })
        {
            if (fileSystem.GetEntry(folder) is { IsLink: true })
            {
                throw new IOException($"{folder} is a link or junction. Amberkeep does not use an archive through links; remove the link.");
            }
        }
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
