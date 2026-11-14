using Amberkeep.Core.Exclusions;
using Amberkeep.Core.Execution;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Paths;
using Amberkeep.Core.Scanning;

namespace Amberkeep.Storage.Archive;

public sealed record SnapshotInfo(string Name, string Path, DateTimeOffset Time, int FileCount, long Bytes);

public sealed record ArchivedFile(string RelativePath, long Length, DateTime LastWriteTimeUtc);

public sealed record RestoreProgress(int Done, int Total, string CurrentPath);

public sealed record RestoreResult(int Restored, int SkippedExisting, IReadOnlyList<ExecutionFailure> Failures, bool Cancelled);

/// <summary>Lists archive snapshots of a target and restores files from them (SPEC 4.4). The archive itself is never changed by a restore.</summary>
public sealed class ArchiveBrowser(IWritableFileSystem fileSystem)
{
    private const int BufferSize = 1 << 20;

    /// <param name="targetPath">Target folder.</param>
    /// <param name="withTotals">Count files and bytes of each snapshot (scans them); otherwise both are 0 – see <see cref="Measure"/>.</param>
    public IReadOnlyList<SnapshotInfo> ListSnapshots(string targetPath, bool withTotals = true)
    {
        var root = ArchiveCleaner.ArchiveRoot(targetPath);
        if (!fileSystem.DirectoryExists(root))
        {
            return [];
        }

        var snapshots = new List<SnapshotInfo>();
        foreach (var entry in fileSystem.ListDirectory(root))
        {
            if (entry.Kind != EntryKind.Directory || entry.IsLink || ArchiveCleaner.ParseSnapshotTime(entry.Name) is not { } time)
            {
                continue;
            }

            var snapshot = new SnapshotInfo(entry.Name, Path.Join(root, entry.Name), time, 0, 0);
            snapshots.Add(withTotals ? Measure(snapshot) : snapshot);
        }

        return snapshots.OrderByDescending(s => s.Time).ThenByDescending(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The snapshot with its file count and total size.</summary>
    public SnapshotInfo Measure(SnapshotInfo snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var files = new Scanner(fileSystem).Scan(snapshot.Path, ExclusionMatcher.None).Entries.Where(e => e.Kind == EntryKind.File).ToList();
        return snapshot with { FileCount = files.Count, Bytes = files.Sum(f => f.Length) };
    }

    public IReadOnlyList<ArchivedFile> ListFiles(SnapshotInfo snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new Scanner(fileSystem).Scan(snapshot.Path, ExclusionMatcher.None).Entries
            .Where(e => e.Kind == EntryKind.File)
            .Select(e => new ArchivedFile(e.RelativePath, e.Length, e.LastWriteTimeUtc))
            .OrderBy(f => f.RelativePath, PathOrderComparer.Instance)
            .ToList();
    }

    /// <summary>Copies files from a snapshot into <paramref name="destinationFolder"/>, keeping their relative paths.</summary>
    /// <param name="snapshot">Snapshot to restore from.</param>
    /// <param name="relativePaths">Files to restore ('/' separated, as listed by <see cref="ListFiles"/>).</param>
    /// <param name="destinationFolder">Where to restore; must not be inside a <c>.amberkeep</c> folder.</param>
    /// <param name="overwrite">Replace files that already exist; otherwise they are skipped.</param>
    /// <param name="progress">Optional progress.</param>
    /// <param name="cancellationToken">Stops between files and during copying; no partial file is left behind.</param>
    public async Task<RestoreResult> RestoreAsync(
        SnapshotInfo snapshot,
        IReadOnlyCollection<string> relativePaths,
        string destinationFolder,
        bool overwrite,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(relativePaths);
        var destination = Path.GetFullPath(destinationFolder);
        if (IsInsideProgramFolder(destination))
        {
            throw new ArgumentException("Files cannot be restored into a .amberkeep folder.", nameof(destinationFolder));
        }

        int restored = 0, skipped = 0, done = 0;
        var failures = new List<ExecutionFailure>();

        foreach (var relative in relativePaths)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new RestoreResult(restored, skipped, failures, Cancelled: true);
            }

            progress?.Report(new RestoreProgress(done, relativePaths.Count, relative));
            var source = PathKey.ToSystemPath(snapshot.Path, relative);
            var target = PathKey.ToSystemPath(destination, relative);
            try
            {
                var existing = fileSystem.GetEntry(target);
                if (existing is { Kind: EntryKind.Directory })
                {
                    throw new IOException("A folder with this name exists at the destination.");
                }

                if (existing is not null && !overwrite)
                {
                    skipped++;
                    continue;
                }

                await CopyAsync(source, target, cancellationToken).ConfigureAwait(false);
                restored++;
            }
            catch (OperationCanceledException)
            {
                return new RestoreResult(restored, skipped, failures, Cancelled: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new ExecutionFailure(relative, ExecutionOperation.Copy, ex.Message));
            }
            finally
            {
                done++;
            }
        }

        progress?.Report(new RestoreProgress(done, relativePaths.Count, string.Empty));
        return new RestoreResult(restored, skipped, failures, Cancelled: false);
    }

    /// <summary>Deletes one snapshot with all its files.</summary>
    /// <exception cref="InvalidOperationException">The path is not a snapshot in an Amberkeep archive.</exception>
    public void DeleteSnapshot(SnapshotInfo snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshot.Path));
        var parent = Path.GetDirectoryName(full);
        var programFolder = parent is null ? null : Path.GetDirectoryName(parent);
        if (ArchiveCleaner.ParseSnapshotTime(Path.GetFileName(full)) is null
            || !string.Equals(Path.GetFileName(parent), "archive", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(programFolder), Scanner.ProgramFolderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Not an archive snapshot: {snapshot.Path}");
        }

        fileSystem.DeleteDirectoryTree(full);
    }

    /// <summary>Deletes snapshots older than <paramref name="retention"/>, like the automatic clean-up after a run.</summary>
    public IReadOnlyList<string> PurgeOld(string targetPath, TimeSpan retention, DateTimeOffset now) =>
        ArchiveCleaner.PurgeSnapshots(fileSystem, targetPath, now - retention);

    private async Task CopyAsync(string source, string target, CancellationToken cancellationToken)
    {
        fileSystem.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = $"{target}.amberkeep-restore-{Guid.NewGuid():N}";
        try
        {
            await using (var input = fileSystem.OpenRead(source))
            await using (var output = fileSystem.CreateFile(temp))
            {
                await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
            }

            fileSystem.CopyMetadata(source, temp);
            if (fileSystem.GetEntry(target) is not null)
            {
                var attributes = fileSystem.GetAttributes(target);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    fileSystem.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
                }
            }

            fileSystem.MoveFile(temp, target, overwrite: true);
        }
        catch
        {
            try
            {
                if (fileSystem.GetEntry(temp) is not null)
                {
                    fileSystem.SetAttributes(temp, FileAttributes.Normal);
                    fileSystem.DeleteFile(temp);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave the temporary file; the destination is unchanged.
            }

            throw;
        }
    }

    private static bool IsInsideProgramFolder(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, Scanner.ProgramFolderName, StringComparison.OrdinalIgnoreCase));
}
