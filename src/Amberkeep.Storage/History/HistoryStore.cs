using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberkeep.Core.FileSystem;

namespace Amberkeep.Storage.History;

/// <summary>
/// Run records in <c>history/</c> next to the executable: a small summary file per run
/// (<c>&lt;name&gt;.json</c>) and the list of selected changes beside it (<c>&lt;name&gt;.changes.json</c>).
/// </summary>
public sealed partial class HistoryStore(string directory, IWritableFileSystem fileSystem)
{
    private const string RecordExtension = ".json";
    private const string ChangesExtension = ".changes.json";

    /// <summary>Names Amberkeep writes: <c>yyyyMMdd-HHmmss-xxxxxxxx.json</c>, its <c>.changes.json</c>, and interrupted saves.</summary>
    [GeneratedRegex(@"^\d{8}-\d{6}-[0-9a-f]{8}(\.changes)?\.json(\.[0-9a-f]{32}\.tmp)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OwnFileName();

    [GeneratedRegex(@"^\d{8}-\d{6}-[0-9a-f]{8}\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RecordFileName();

    /// <summary>
    /// Deletes every history file Amberkeep wrote, and the folder when nothing else is left in it.
    /// Files it did not write are kept, and a folder that is a link is not touched at all.
    /// </summary>
    /// <returns>Problems; empty when everything was removed.</returns>
    public IReadOnlyList<string> DeleteOwnFiles()
    {
        var problems = new List<string>();
        if (fileSystem.GetEntry(Directory) is not { Kind: EntryKind.Directory } folder)
        {
            return problems;
        }

        if (folder.IsLink)
        {
            problems.Add($"{Directory} is a link or junction and was left as it is.");
            return problems;
        }

        foreach (var entry in fileSystem.ListDirectory(Directory).Where(e => e.Kind == EntryKind.File && OwnFileName().IsMatch(e.Name)))
        {
            try
            {
                fileSystem.DeleteFile(Path.Join(Directory, entry.Name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{entry.Name}: {ex.Message}");
            }
        }

        if (fileSystem.IsDirectoryEmpty(Directory))
        {
            fileSystem.DeleteEmptyDirectory(Directory);
        }

        return problems;
    }

    public HistoryStore(AppStore store, IWritableFileSystem fileSystem)
        : this(store.HistoryDirectory, fileSystem)
    {
    }

    public string Directory { get; } = directory;

    /// <summary>Saves the record and deletes the oldest records beyond <paramref name="limit"/>.</summary>
    public void Add(RunRecord record, int limit)
    {
        ArgumentNullException.ThrowIfNull(record);
        var name = BaseName(record);
        StorageFiles.Write(fileSystem, Path.Join(Directory, name + ChangesExtension), record.SelectedChanges.ToList(), StorageJsonContext.Default.ListRecordedChange);
        StorageFiles.Write(fileSystem, Path.Join(Directory, name + RecordExtension), record, StorageJsonContext.Default.RunRecord);
        Prune(Math.Max(1, limit));
    }

    /// <summary>All readable records without their change lists, newest first. Damaged files are skipped.</summary>
    public IReadOnlyList<RunRecord> List()
    {
        var records = new List<RunRecord>();
        foreach (var path in RecordFiles())
        {
            if (TryRead(path) is { } record)
            {
                records.Add(record);
            }
        }

        return records.OrderByDescending(r => r.StartedUtc).ToList();
    }

    public RunRecord? LatestFor(Guid jobId, Guid targetId) =>
        List().FirstOrDefault(r => r.JobId == jobId && r.TargetId == targetId);

    /// <summary>The selected changes of a run; empty when the file is missing or damaged.</summary>
    public IReadOnlyList<RecordedChange> ReadChanges(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = Path.Join(Directory, BaseName(record) + ChangesExtension);
        try
        {
            return fileSystem.FileExists(path)
                ? StorageFiles.Read(fileSystem, path, StorageJsonContext.Default.ListRecordedChange) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Deletes all records of a job; returns how many were deleted.</summary>
    public int DeleteForJob(Guid jobId)
    {
        var deleted = 0;
        foreach (var path in RecordFiles())
        {
            if (TryRead(path)?.JobId == jobId)
            {
                Delete(path);
                deleted++;
            }
        }

        return deleted;
    }

    private void Prune(int limit)
    {
        // File names start with the start time, so ordinal order is chronological.
        foreach (var path in RecordFiles().OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal).Skip(limit))
        {
            Delete(path);
        }

        // Leftovers of interrupted saves: change lists without their record, and temporary files.
        var names = fileSystem.ListDirectory(Directory).Where(e => e.Kind == EntryKind.File).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Where(n => OwnFileName().IsMatch(n) && !RecordFileName().IsMatch(n)))
        {
            var orphanChanges = name.EndsWith(ChangesExtension, StringComparison.OrdinalIgnoreCase)
                                && !names.Contains(name[..^ChangesExtension.Length] + RecordExtension);
            if (orphanChanges || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                fileSystem.DeleteFile(Path.Join(Directory, name));
            }
        }
    }

    /// <summary>Deletes a record file together with its change list.</summary>
    private void Delete(string recordPath)
    {
        fileSystem.DeleteFile(recordPath);
        var changes = recordPath[..^RecordExtension.Length] + ChangesExtension;
        if (fileSystem.FileExists(changes))
        {
            fileSystem.DeleteFile(changes);
        }
    }

    private RunRecord? TryRead(string path)
    {
        try
        {
            return StorageFiles.Read(fileSystem, path, StorageJsonContext.Default.RunRecord);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged record does not hide the others.
            return null;
        }
    }

    private static string BaseName(RunRecord record) => string.Create(
        CultureInfo.InvariantCulture,
        $"{record.StartedUtc.UtcDateTime:yyyyMMdd-HHmmss}-{record.RunId.ToString("N")[..8]}");

    private IEnumerable<string> RecordFiles() =>
        fileSystem.DirectoryExists(Directory)
            ? fileSystem.ListDirectory(Directory)
                .Where(e => e.Kind == EntryKind.File && RecordFileName().IsMatch(e.Name))
                .Select(e => Path.Join(Directory, e.Name))
                .ToList()
            : [];
}
