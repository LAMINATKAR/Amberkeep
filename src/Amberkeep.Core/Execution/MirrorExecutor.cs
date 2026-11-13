using System.Buffers;
using System.Globalization;
using Amberkeep.Core.Diff;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Paths;
using Amberkeep.Core.Safety;
using Amberkeep.Core.Scanning;

namespace Amberkeep.Core.Execution;

/// <summary>
/// Applies the selected items of a <see cref="DiffResult"/> to the target (SPEC 4.2, 4.4, 4.5).
/// Writes only under the target folder; the source is only read.
/// </summary>
public sealed class MirrorExecutor(IWritableFileSystem fileSystem, TimeProvider timeProvider)
{
    private const int CopyBufferSize = 1 << 20;
    private const string RunStampFormat = "yyyyMMddHHmmss";
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(24);

    public MirrorExecutor()
        : this(PhysicalFileSystem.Instance, TimeProvider.System)
    {
    }

    public MirrorExecutor(IWritableFileSystem fileSystem)
        : this(fileSystem, TimeProvider.System)
    {
    }

    /// <param name="diff">Result of <see cref="DiffEngine.AnalyzeAsync"/>.</param>
    /// <param name="selection">Items the user selected; <see cref="DiffCategory.Error"/> items are ignored.</param>
    /// <param name="options">Archive, approval and verification settings.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancels between operations and during copying; nothing is left half-written.</param>
    public Task<ExecutionResult> ExecuteAsync(
        DiffResult diff,
        IReadOnlySet<DiffItem> selection,
        ExecutionOptions options,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options);

        return Task.Run(() => new Run(fileSystem, timeProvider, diff, options, progress, cancellationToken).Execute(selection));
    }

    /// <summary>The target changed since the analysis; the item is skipped.</summary>
    private sealed class StaleItemException(string message) : Exception(message);

    private sealed class Run(
        IWritableFileSystem fs,
        TimeProvider time,
        DiffResult diff,
        ExecutionOptions options,
        IProgress<ExecutionProgress>? progress,
        CancellationToken ct)
    {
        private readonly WriteGuard _guard = new(diff.SourcePath, diff.TargetPath);
        private readonly string _programFolder = Path.Join(diff.TargetPath, Scanner.ProgramFolderName);
        private readonly List<ExecutionFailure> _failures = [];
        private readonly List<string> _notes = [];
        private readonly long _started = time.GetTimestamp();
        private long _lastReport;
        private string? _archiveSnapshot;
        private int _completed;
        private int _total;
        private long _totalBytes;
        private int _copied, _replaced, _removedFiles, _removedDirs, _createdDirs, _renamed, _timestamps, _archived;
        private long _copiedBytes;
        private long _progressBytes;
        private bool _freeSpaceUnknown;

        // Each run copies into its own folder, so a second run on the same target never touches its files.
        private readonly string _runId =
            time.GetUtcNow().ToString(RunStampFormat, CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");

        private string TempRoot => Path.Join(_programFolder, "tmp");

        private string TempFolder => Path.Join(TempRoot, _runId);

        private string ArchiveRoot => Path.Join(_programFolder, "archive");

        public ExecutionResult Execute(IReadOnlySet<DiffItem> selection)
        {
            var selected = diff.Items.Where(i => i.Category != DiffCategory.Error && selection.Contains(i)).ToList();

            var refusal = CheckRefusal(selected);
            if (refusal is not null)
            {
                return Refused(refusal);
            }

            if (selected.Count == 0)
            {
                return Result(ExecutionOutcome.Completed);
            }

            try
            {
                Prepare();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refused($"The target folder cannot be prepared: {ex.Message}");
            }

            _total = selected.Count;
            _totalBytes = selected.Where(IsCopy).Sum(i => i.Source!.Length);

            var cancelled = false;
            try
            {
                RemoveEntries(selected);
                RenameEntries(selected);
                CreateDirectories(selected);
                CopyFiles(selected);
                UpdateTimestamps(selected);
                CleanArchive();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled = true;
            }
            finally
            {
                CleanUpProgramFolder();
            }

            Report(ExecutionOperation.Copy, string.Empty, force: true);
            return Result(cancelled
                ? ExecutionOutcome.Cancelled
                : _failures.Count > 0 ? ExecutionOutcome.CompletedWithErrors : ExecutionOutcome.Completed);
        }

        private string? CheckRefusal(List<DiffItem> selected)
        {
            if (diff.IsBlocked)
            {
                var reasons = diff.SafetyIssues.Where(i => i.Severity == SafetySeverity.Blocking).Select(i => i.Message);
                return "The analysis found a blocking problem: " + string.Join(" ", reasons);
            }

            var removedFiles = selected.Count(i => i.Category == DiffCategory.Removed && i.Kind == EntryKind.File);
            var massRemoval = SafetyChecks.CheckMassRemoval(removedFiles, diff.TargetFileCount, diff.Options.MassRemovalGuard);
            if (massRemoval is not null && !options.Approved)
            {
                return massRemoval.Message + " The run must be approved.";
            }

            var required = DiffSummary.Compute(selected, _ => true, 0).RequiredFreeSpace(options.ArchiveEnabled);
            if (required > 0)
            {
                var existing = NearestExistingFolder(diff.TargetPath);
                var free = existing is null ? null : fs.GetAvailableFreeSpace(existing);
                if (free is null)
                {
                    _freeSpaceUnknown = true;
                }
                else if (free < required)
                {
                    return $"Not enough free space on the target: {required:N0} bytes needed, {free:N0} available.";
                }
            }

            return null;
        }

        private void Prepare()
        {
            if (fs.GetEntry(diff.TargetPath) is null)
            {
                fs.CreateDirectory(_guard.Check(diff.TargetPath));
            }

            fs.CreateDirectory(_guard.Check(TempFolder));
            DeleteLeftoverTempFiles();

            if (_freeSpaceUnknown)
            {
                _notes.Add("Free space on the target could not be checked.");
            }
        }

        /// <summary>
        /// Removes temporary copies of interrupted runs. Folders of runs started in the last 24 hours are kept,
        /// because another run may still be using them.
        /// </summary>
        private void DeleteLeftoverTempFiles()
        {
            var cutoff = time.GetUtcNow() - LeftoverAge;
            foreach (var entry in fs.ListDirectory(TempRoot))
            {
                if (entry.Name == _runId || (entry.Kind == EntryKind.Directory && ParseRunStamp(entry.Name) > cutoff))
                {
                    continue;
                }

                var path = _guard.Check(Path.Join(TempRoot, entry.Name));
                try
                {
                    if (entry.Kind == EntryKind.Directory && !entry.IsLink)
                    {
                        fs.DeleteDirectoryTree(path);
                    }
                    else
                    {
                        ClearReadOnly(path);
                        fs.DeleteFile(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _notes.Add($"Old temporary files could not be removed: {ex.Message}");
                }
            }
        }

        private static DateTimeOffset? ParseRunStamp(string name) =>
            name.Length > RunStampFormat.Length
            && DateTimeOffset.TryParseExact(
                name[..RunStampFormat.Length],
                RunStampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var stamp)
                ? stamp
                : null;

        private void RemoveEntries(List<DiffItem> selected)
        {
            var removals = selected.Where(i => i.Category == DiffCategory.Removed).ToList();

            foreach (var item in removals.Where(i => i.Kind == EntryKind.File))
            {
                Do(item, ExecutionOperation.Remove, () =>
                {
                    var path = TargetPath(item.Target!.RelativePath);
                    EnsureUnchanged(item.Target, path);
                    DisposeOfTargetFile(item.Target.RelativePath, path);
                    _removedFiles++;
                });
            }

            // Deepest first, so that a folder is empty by the time it is removed.
            foreach (var item in removals.Where(i => i.Kind == EntryKind.Directory).OrderByDescending(i => Depth(i.RelativePath)))
            {
                Do(item, ExecutionOperation.Remove, () =>
                {
                    var path = TargetPath(item.Target!.RelativePath);
                    var entry = fs.GetEntry(path);
                    if (entry is null)
                    {
                        return;
                    }

                    if (entry.Kind != EntryKind.Directory || entry.IsLink)
                    {
                        throw new StaleItemException("Changed since analysis – analyze again.");
                    }

                    if (!fs.IsDirectoryEmpty(path))
                    {
                        _notes.Add($"Folder kept because it is not empty: {item.RelativePath}");
                        return;
                    }

                    fs.DeleteEmptyDirectory(_guard.Check(path));
                    _removedDirs++;
                });
            }
        }

        private void RenameEntries(List<DiffItem> selected)
        {
            var renames = selected
                .Where(i => i.Category == DiffCategory.CaseRename)
                .OrderBy(i => i.Kind == EntryKind.File)
                .ThenBy(i => Depth(i.RelativePath));

            foreach (var item in renames)
            {
                Do(item, ExecutionOperation.Rename, () =>
                {
                    var path = TargetPath(item.Target!.RelativePath);
                    EnsureUnchanged(item.Target, path);
                    RenameLeaf(path, PathKey.GetName(item.Source!.RelativePath), item.Kind);
                    _renamed++;
                });
            }
        }

        private void CreateDirectories(List<DiffItem> selected)
        {
            var directories = selected
                .Where(i => i.Category == DiffCategory.New && i.Kind == EntryKind.Directory)
                .OrderBy(i => Depth(i.RelativePath));

            foreach (var item in directories)
            {
                Do(item, ExecutionOperation.CreateDirectory, () =>
                {
                    var path = TargetPath(item.Source!.RelativePath);
                    var entry = fs.GetEntry(path);
                    if (entry is { Kind: EntryKind.Directory, IsLink: false })
                    {
                        return;
                    }

                    if (entry is not null)
                    {
                        throw new StaleItemException("Something else now exists at this path on the target – analyze again.");
                    }

                    fs.CreateDirectory(_guard.Check(path));
                    _createdDirs++;
                });
            }
        }

        private void CopyFiles(List<DiffItem> selected)
        {
            foreach (var item in selected.Where(IsCopy))
            {
                Do(item, ExecutionOperation.Copy, () => CopyFile(item));
            }
        }

        private void CopyFile(DiffItem item)
        {
            var source = SourcePath(item.Source!.RelativePath);
            var destination = TargetPath(item.Source.RelativePath);
            EnsureUnchanged(item.Source, source);

            string? existing = null;
            if (item.Category == DiffCategory.New)
            {
                if (fs.GetEntry(destination) is not null)
                {
                    throw new StaleItemException("Something now exists at this path on the target – analyze again.");
                }
            }
            else
            {
                existing = TargetPath(item.Target!.RelativePath);
                EnsureUnchanged(item.Target, existing);
            }

            var parent = Path.GetDirectoryName(destination)!;
            if (fs.GetEntry(parent) is null)
            {
                // The parent folder may be unselected in the diff; a selected file still needs it.
                fs.CreateDirectory(_guard.Check(parent));
                _createdDirs++;
            }

            var temp = Path.Join(TempFolder, Guid.NewGuid().ToString("N"));
            try
            {
                CopyContent(source, temp, item.RelativePath);

                if (!Matches(item.Source, fs.GetEntry(source)))
                {
                    throw new StaleItemException("Source changed during copying – analyze again.");
                }

                if (options.VerifyCopies && !ContentComparer.AreEqual(fs, source, temp, ct))
                {
                    throw new IOException("Verification failed: the copy differs from the source.");
                }

                fs.CopyMetadata(source, _guard.Check(temp));
                PlaceCopy(item, temp, destination, existing);
            }
            catch
            {
                TryDeleteFile(temp);
                throw;
            }

            if (existing is null)
            {
                _copied++;
            }
            else
            {
                _replaced++;
            }

            // Counted only once the copy is in place; failed or cancelled copies are not "written".
            _copiedBytes += item.Source.Length;
        }

        private void CopyContent(string source, string temp, string relativePath)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                using var input = fs.OpenRead(source);
                using var output = fs.CreateFile(_guard.Check(temp));
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = input.Read(buffer, 0, CopyBufferSize);
                    if (read == 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, read);
                    _progressBytes += read;
                    Report(ExecutionOperation.Copy, relativePath, force: false);
                }

                if (output is FileStream file)
                {
                    file.Flush(flushToDisk: true);
                }
                else
                {
                    output.Flush();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>Moves the finished copy into place, archiving or deleting the version it replaces.</summary>
        private void PlaceCopy(DiffItem item, string temp, string destination, string? existing)
        {
            if (existing is null)
            {
                fs.MoveFile(temp, _guard.Check(destination), overwrite: false);
                return;
            }

            if (options.ArchiveEnabled)
            {
                var archived = MoveToArchive(item.Target!.RelativePath, existing);
                try
                {
                    fs.MoveFile(temp, _guard.Check(destination), overwrite: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Put the old version back so the target is not left without the file.
                    fs.MoveFile(_guard.Check(archived), _guard.Check(existing), overwrite: false);
                    _archived--;
                    throw;
                }

                return;
            }

            ClearReadOnly(existing);
            // Same leaf name (a parent folder may differ only in case): the paths point to the same file.
            if (string.Equals(Path.GetFileName(existing), Path.GetFileName(destination), StringComparison.Ordinal))
            {
                // Atomic replace: the old content stays until the new one is in place.
                fs.MoveFile(temp, _guard.Check(destination), overwrite: true);
            }
            else
            {
                // Name differs in letter case: remove the old name, then place the new one.
                fs.DeleteFile(_guard.Check(existing));
                fs.MoveFile(temp, _guard.Check(destination), overwrite: false);
            }
        }

        private void UpdateTimestamps(List<DiffItem> selected)
        {
            foreach (var item in selected.Where(i => i.Category == DiffCategory.TimestampOnly))
            {
                Do(item, ExecutionOperation.UpdateTimestamp, () =>
                {
                    var source = SourcePath(item.Source!.RelativePath);
                    var path = TargetPath(item.Target!.RelativePath);
                    EnsureUnchanged(item.Source, source);
                    EnsureUnchanged(item.Target, path);

                    var attributes = fs.GetAttributes(path);
                    var readOnly = (attributes & FileAttributes.ReadOnly) != 0;
                    if (readOnly)
                    {
                        fs.SetAttributes(_guard.Check(path), attributes & ~FileAttributes.ReadOnly);
                    }

                    fs.SetLastWriteTimeUtc(_guard.Check(path), item.Source.LastWriteTimeUtc);
                    if (readOnly)
                    {
                        fs.SetAttributes(_guard.Check(path), attributes);
                    }

                    var name = PathKey.GetName(item.Source.RelativePath);
                    if (!string.Equals(PathKey.GetName(item.Target.RelativePath), name, StringComparison.Ordinal))
                    {
                        RenameLeaf(path, name, EntryKind.File);
                    }

                    _timestamps++;
                });
            }
        }

        private void CleanArchive()
        {
            if (!options.ArchiveEnabled || options.ArchiveRetention is not { } retention)
            {
                return;
            }

            try
            {
                var cutoff = time.GetUtcNow() - retention;
                var keep = _archiveSnapshot is null ? null : Path.GetFileName(_archiveSnapshot);
                foreach (var name in ArchiveCleaner.Purge(fs, _guard, ArchiveRoot, cutoff, keep))
                {
                    _notes.Add($"Old archive snapshot deleted: {name}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _failures.Add(new ExecutionFailure(string.Empty, ExecutionOperation.CleanArchive, ex.Message));
            }
        }

        private void CleanUpProgramFolder()
        {
            try
            {
                if (fs.DirectoryExists(TempFolder))
                {
                    fs.DeleteDirectoryTree(_guard.Check(TempFolder));
                }

                if (fs.DirectoryExists(TempRoot) && fs.IsDirectoryEmpty(TempRoot))
                {
                    fs.DeleteEmptyDirectory(_guard.Check(TempRoot));
                }

                if (fs.DirectoryExists(_programFolder) && fs.IsDirectoryEmpty(_programFolder))
                {
                    fs.DeleteEmptyDirectory(_guard.Check(_programFolder));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _notes.Add($"Temporary files could not be removed: {ex.Message}");
            }
        }

        /// <summary>Removed or overwritten file: to the archive, or deleted.</summary>
        private void DisposeOfTargetFile(string relativePath, string path)
        {
            if (options.ArchiveEnabled)
            {
                MoveToArchive(relativePath, path);
                return;
            }

            ClearReadOnly(path);
            fs.DeleteFile(_guard.Check(path));
        }

        private string MoveToArchive(string relativePath, string path)
        {
            var destination = PathKey.ToSystemPath(ArchiveSnapshot(), relativePath);
            fs.CreateDirectory(_guard.Check(Path.GetDirectoryName(destination)!));
            fs.MoveFile(_guard.Check(path), _guard.Check(destination), overwrite: false);
            _archived++;
            return destination;
        }

        private string ArchiveSnapshot()
        {
            if (_archiveSnapshot is null)
            {
                var name = ArchiveCleaner.SnapshotName(time.GetUtcNow());
                var path = Path.Join(ArchiveRoot, name);
                for (var n = 2; fs.GetEntry(path) is not null; n++)
                {
                    path = Path.Join(ArchiveRoot, $"{name}-{n}");
                }

                fs.CreateDirectory(_guard.Check(path));
                _archiveSnapshot = path;
            }

            return _archiveSnapshot;
        }

        /// <summary>Changes only the letter case of the last path segment, via a temporary sibling name.</summary>
        private void RenameLeaf(string path, string newName, EntryKind kind)
        {
            var folder = Path.GetDirectoryName(path)!;
            var destination = Path.Join(folder, newName);
            var temp = Path.Join(folder, $".{newName}.amberkeep-{Guid.NewGuid():N}");

            Move(path, temp);
            try
            {
                Move(temp, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Move(temp, path);
                throw;
            }

            void Move(string from, string to)
            {
                if (kind == EntryKind.File)
                {
                    fs.MoveFile(_guard.Check(from), _guard.Check(to), overwrite: false);
                }
                else
                {
                    fs.MoveDirectory(_guard.Check(from), _guard.Check(to));
                }
            }
        }

        private void Do(DiffItem item, ExecutionOperation operation, Action action)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                action();
            }
            catch (StaleItemException ex)
            {
                _failures.Add(new ExecutionFailure(item.RelativePath, operation, ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _failures.Add(new ExecutionFailure(item.RelativePath, operation, ex.Message));
            }

            _completed++;
            Report(operation, item.RelativePath, force: false);
        }

        private void EnsureUnchanged(ScannedEntry expected, string path)
        {
            if (!Matches(expected, fs.GetEntry(path)))
            {
                throw new StaleItemException("Changed since analysis – analyze again.");
            }
        }

        // Times within the compare tolerance count as equal: directory listings can report slightly stale
        // times, and the diff itself treats such files as equal.
        private bool Matches(ScannedEntry expected, FileSystemEntry? actual) =>
            actual is not null
            && !actual.IsLink
            && actual.Kind == expected.Kind
            && (expected.Kind == EntryKind.Directory
                || (actual.Length == expected.Length
                    && (actual.LastWriteTimeUtc - expected.LastWriteTimeUtc).Duration() <= diff.Options.TimeTolerance));

        private void ClearReadOnly(string path)
        {
            var attributes = fs.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                fs.SetAttributes(_guard.Check(path), attributes & ~FileAttributes.ReadOnly);
            }
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                if (fs.GetEntry(path) is not null)
                {
                    ClearReadOnly(path);
                    fs.DeleteFile(_guard.Check(path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The temp folder is deleted at the end of the run anyway.
            }
        }

        private string? NearestExistingFolder(string path)
        {
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if (fs.DirectoryExists(current))
                {
                    return current;
                }
            }

            return null;
        }

        private void Report(ExecutionOperation operation, string relativePath, bool force)
        {
            if (progress is null)
            {
                return;
            }

            var now = time.GetTimestamp();
            if (!force && time.GetElapsedTime(_lastReport, now) < ProgressInterval)
            {
                return;
            }

            _lastReport = now;
            progress.Report(new ExecutionProgress(operation, relativePath, _completed, _total, _progressBytes, _totalBytes));
        }

        private ExecutionResult Refused(string reason) => new()
        {
            Outcome = ExecutionOutcome.Refused,
            RefusedReason = reason,
            Duration = time.GetElapsedTime(_started),
        };

        private ExecutionResult Result(ExecutionOutcome outcome) => new()
        {
            Outcome = outcome,
            CopiedFiles = _copied,
            ReplacedFiles = _replaced,
            RemovedFiles = _removedFiles,
            RemovedDirectories = _removedDirs,
            CreatedDirectories = _createdDirs,
            RenamedEntries = _renamed,
            UpdatedTimestamps = _timestamps,
            ArchivedFiles = _archived,
            CopiedBytes = _copiedBytes,
            Failures = _failures,
            Notes = _notes,
            ArchiveFolder = _archiveSnapshot,
            Duration = time.GetElapsedTime(_started),
        };

        private string SourcePath(string relativePath) => PathKey.ToSystemPath(diff.SourcePath, relativePath);

        private string TargetPath(string relativePath) => PathKey.ToSystemPath(diff.TargetPath, relativePath);

        private static bool IsCopy(DiffItem item) =>
            item.Kind == EntryKind.File
            && item.Category is DiffCategory.New or DiffCategory.Modified or DiffCategory.TargetNewer;

        private static int Depth(string relativePath) => relativePath.Count(c => c == PathKey.Separator);
    }
}
