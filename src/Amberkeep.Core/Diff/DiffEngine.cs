using System.Diagnostics;
using Amberkeep.Core.Exclusions;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Paths;
using Amberkeep.Core.Safety;
using Amberkeep.Core.Scanning;

namespace Amberkeep.Core.Diff;

/// <summary>
/// Compares a source folder with a target folder for one-way mirroring (SPEC 4.1, 4.2, 4.5).
/// Strictly read-only: nothing is written to either side.
/// </summary>
public sealed class DiffEngine(IFileSystem fileSystem)
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    // Move detection: more files than this with one size (and name) on a side are not paired (too ambiguous, and
    // pairing them would cost quadratic time).
    private const int MaxGroup = 64;

    public DiffEngine()
        : this(PhysicalFileSystem.Instance)
    {
    }

    public async Task<DiffResult> AnalyzeAsync(
        DiffRequest request,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetPath);
        ArgumentNullException.ThrowIfNull(request.Options);

        var options = request.Options;
        options.MassRemovalGuard.Validate();
        var caseSensitive = options.CaseSensitivity.IsCaseSensitive();
        var sourceRoot = Path.GetFullPath(request.SourcePath);
        var targetRoot = Path.GetFullPath(request.TargetPath);

        var overlap = SafetyChecks.CheckOverlap(sourceRoot, targetRoot, onDisk: true);
        if (overlap is not null)
        {
            return Blocked(sourceRoot, targetRoot, options, overlap);
        }

        if (!fileSystem.DirectoryExists(sourceRoot))
        {
            return Blocked(sourceRoot, targetRoot, options, SafetyChecks.SourceMissing(sourceRoot));
        }

        if (fileSystem.FileExists(targetRoot))
        {
            return Blocked(sourceRoot, targetRoot, options, SafetyChecks.TargetIsFile(targetRoot));
        }

        var patterns = options.Exclusions.ToList();
        var programFolderNotes = new List<SafetyIssue>();
        foreach (var folder in options.ProtectedFolders)
        {
            var (issue, exclusion) = SafetyChecks.CheckProgramFolder(folder, sourceRoot, targetRoot);
            if (issue is { Severity: SafetySeverity.Blocking })
            {
                return Blocked(sourceRoot, targetRoot, options, issue);
            }

            if (issue is not null && exclusion is not null)
            {
                programFolderNotes.Add(issue);
                patterns.Add(exclusion);
            }
        }

        // A damaged selection never turns into "the whole folder": the run stops instead.
        if (options.IncludedPaths.FirstOrDefault(p => !IncludeFilter.IsValidPath(p)) is { } invalid)
        {
            return Blocked(sourceRoot, targetRoot, options, SafetyChecks.InvalidSelection(invalid));
        }

        var include = options.IncludedPaths.Count == 0 ? null : new IncludeFilter(options.IncludedPaths, caseSensitive);
        var exclusions = new ExclusionMatcher(patterns, caseSensitive);
        var scanner = new Scanner(fileSystem);
        var tracker = new ScanProgressTracker(progress);

        // Source and target are usually on different disks, so scanning them in parallel saves time.
        var sourceScan = Task.Run(
            () => scanner.Scan(sourceRoot, exclusions, tracker.Source, cancellationToken, include), cancellationToken);
        var targetScan = Task.Run(
            () => scanner.Scan(targetRoot, exclusions, tracker.Target, cancellationToken, include), cancellationToken);
        var scans = await Task.WhenAll(sourceScan, targetScan).ConfigureAwait(false);
        var source = scans[0];
        var target = scans[1];
        if (include is not null)
        {
            source = WithMissingSelection(source, include, caseSensitive);
        }

        if (!source.RootExists)
        {
            return Blocked(sourceRoot, targetRoot, options, SafetyChecks.SourceMissing(sourceRoot));
        }

        if (source.RootError is not null)
        {
            return Blocked(sourceRoot, targetRoot, options, SafetyChecks.SourceUnreadable(sourceRoot, source.RootError));
        }

        if (target.RootError is not null)
        {
            return Blocked(sourceRoot, targetRoot, options, SafetyChecks.TargetUnreadable(targetRoot, target.RootError));
        }

        var ignoreHourShift = options.IgnoreOneHourShift && (IsFat(sourceRoot) || IsFat(targetRoot));
        var comparison = new Comparison(fileSystem, options, caseSensitive, ignoreHourShift, sourceRoot, targetRoot);
        comparison.Run(source, target, progress, cancellationToken);

        var items = comparison.Items.OrderBy(i => i.RelativePath, PathOrderComparer.Instance).ToList();
        var sourceFiles = source.FileCount;
        var targetFiles = target.FileCount;

        var safety = new List<SafetyIssue>(programFolderNotes);
        if (!target.RootExists)
        {
            safety.Add(SafetyChecks.TargetMissing(targetRoot));
        }

        AddIfNotNull(safety, SafetyChecks.CheckSourceEmpty(sourceFiles, targetFiles));
        var removedFiles = items.Count(i => i.Category == DiffCategory.Removed && i.Kind == EntryKind.File && i.DefaultSelected);
        AddIfNotNull(safety, SafetyChecks.CheckMassRemoval(removedFiles, targetFiles, options.MassRemovalGuard));

        progress?.Report(new AnalysisProgress(AnalysisStage.Done, source.Entries.Count, target.Entries.Count, 0, 0));

        return new DiffResult
        {
            SourcePath = sourceRoot,
            TargetPath = targetRoot,
            Options = options,
            Items = items,
            Errors = comparison.Errors,
            Skipped = comparison.Skipped,
            SafetyIssues = safety,
            Summary = DiffSummary.Compute(items, i => i.DefaultSelected, comparison.UnchangedFiles),
            SourceFileCount = sourceFiles,
            TargetFileCount = targetFiles,
        };
    }

    private static DiffResult Blocked(string sourceRoot, string targetRoot, DiffOptions options, SafetyIssue issue) => new()
    {
        SourcePath = sourceRoot,
        TargetPath = targetRoot,
        Options = options,
        SafetyIssues = [issue],
        Summary = DiffSummary.Compute([], _ => true, 0),
    };

    /// <summary>
    /// A selected folder or file that is not in the source (renamed, deleted, or excluded) is an error of its own: its
    /// content is unknown, so nothing under its path is changed on the target, as for an unreadable folder.
    /// </summary>
    private static ScanResult WithMissingSelection(ScanResult source, IncludeFilter include, bool caseSensitive)
    {
        if (!source.RootExists || source.RootError is not null)
        {
            return source;
        }

        var found = new HashSet<string>(
            source.Entries.Select(e => PathKey.Normalize(e.RelativePath)),
            caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var missing = include.Paths
            .Where(p => !found.Contains(p))
            .Select(p => new ScanIssue(p, "Selected for this backup, but not found in the source (renamed, deleted or excluded?). Nothing under it is changed on the target; edit the backup to update the selection."))
            .ToList();
        return missing.Count == 0
            ? source
            : new ScanResult
            {
                RootPath = source.RootPath,
                RootExists = source.RootExists,
                Entries = source.Entries,
                Errors = [.. source.Errors, .. missing],
                Skipped = source.Skipped,
                FileCount = source.FileCount,
            };
    }

    // FAT12/16/32 store local time; exFAT and NTFS store UTC (exFAT with an offset).
    private bool IsFat(string path) =>
        fileSystem.GetVolumeFormat(path) is { } format && format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase);

    private static void AddIfNotNull(List<SafetyIssue> list, SafetyIssue? issue)
    {
        if (issue is not null)
        {
            list.Add(issue);
        }
    }

    /// <summary>Matches the two scans and classifies every entry.</summary>
    private sealed class Comparison(
        IFileSystem fileSystem,
        DiffOptions options,
        bool caseSensitive,
        bool ignoreHourShift,
        string sourceRoot,
        string targetRoot)
    {
        private readonly StringComparer _comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        private readonly HashSet<DiffItem> _replacedByOtherKind = new(ReferenceEqualityComparer.Instance);

        public List<DiffItem> Items { get; } = [];

        public List<DiffIssue> Errors { get; } = [];

        public List<DiffIssue> Skipped { get; } = [];

        public int UnchangedFiles { get; private set; }

        public void Run(
            ScanResult source,
            ScanResult target,
            IProgress<AnalysisProgress>? progress,
            CancellationToken cancellationToken)
        {
            AddIssues(source, DiffSide.Source);
            AddIssues(target, DiffSide.Target);

            var collisions = new HashSet<string>(_comparer);
            var sourceMap = BuildMap(source, DiffSide.Source, collisions);
            var targetMap = BuildMap(target, DiffSide.Target, collisions);

            // Paths whose content is unknown on one side (unreadable directory or link that is not followed).
            // Nothing under them may be removed from or written to the target.
            var unknownOnSource = UnknownPaths(source);
            var unknownOnTarget = UnknownPaths(target);

            // Two names in one folder that are the same name here (case, Unicode form): which one is meant is unknown,
            // so neither is compared, copied or removed.
            foreach (var key in collisions)
            {
                sourceMap.Remove(key);
                targetMap.Remove(key);
                unknownOnSource.Add(key);
                unknownOnTarget.Add(key);
            }

            var toCompare = new List<(ScannedEntry Source, ScannedEntry Target, bool Renamed)>();

            foreach (var (key, sourceEntry) in sourceMap)
            {
                if (PathKey.IsSelfOrUnder(unknownOnTarget, key))
                {
                    continue;
                }

                if (!targetMap.TryGetValue(key, out var targetEntry))
                {
                    Items.Add(Item(DiffCategory.New, sourceEntry, null));
                    continue;
                }

                if (sourceEntry.Kind != targetEntry.Kind)
                {
                    // A file replaced a directory or vice versa: remove the old entry, then create the new one.
                    // The old file must make room first, so it is never paired as a move.
                    var replacedByOtherKind = Item(DiffCategory.Removed, null, targetEntry);
                    _replacedByOtherKind.Add(replacedByOtherKind);
                    Items.Add(replacedByOtherKind);
                    Items.Add(Item(DiffCategory.New, sourceEntry, null));
                    continue;
                }

                var renamed = !NamesEqual(sourceEntry, targetEntry);
                if (sourceEntry.Kind == EntryKind.Directory)
                {
                    if (renamed)
                    {
                        Items.Add(Item(DiffCategory.CaseRename, sourceEntry, targetEntry));
                    }

                    continue;
                }

                if (options.CompareMode == CompareMode.Content && sourceEntry.Length == targetEntry.Length)
                {
                    toCompare.Add((sourceEntry, targetEntry, renamed));
                    continue;
                }

                AddFileResult(sourceEntry, targetEntry, renamed, contentEqual: null);
            }

            foreach (var (key, targetEntry) in targetMap)
            {
                if (!sourceMap.ContainsKey(key) && !PathKey.IsSelfOrUnder(unknownOnSource, key))
                {
                    Items.Add(Item(DiffCategory.Removed, null, targetEntry));
                }
            }

            CompareContents(toCompare, source, target, progress, cancellationToken);
            DetectMoves(source, target, progress, cancellationToken);
        }

        /// <summary>
        /// Pairs files that are only on the source (New) with files that are only on the target (Removed) when they
        /// are the same file in another place: a renamed folder, a file moved to another folder, a renamed file. Such a
        /// pair becomes one <see cref="DiffCategory.Moved"/> item, so the target moves the file instead of copying it
        /// again and deleting the old one.
        /// </summary>
        /// <remarks>
        /// A pair must be unambiguous: equal size (not empty) and modification time, and no other candidate with the
        /// same size and time (and name) on either side. With the same file name, size and time are enough; a renamed
        /// file (another name) is also compared byte by byte, as is every pair when the job compares by content. An
        /// ambiguous or unconfirmed pair stays New + Removed, so a wrong pair can never put wrong content into the backup.
        /// </remarks>
        private void DetectMoves(
            ScanResult source,
            ScanResult target,
            IProgress<AnalysisProgress>? progress,
            CancellationToken cancellationToken)
        {
            var added = Items.Where(i => i is { Category: DiffCategory.New, Kind: EntryKind.File, Source.Length: > 0 }).ToList();
            var removed = Items
                .Where(i => i is { Category: DiffCategory.Removed, Kind: EntryKind.File, Target.Length: > 0 } && !_replacedByOtherKind.Contains(i))
                .ToList();
            if (added.Count == 0 || removed.Count == 0)
            {
                return;
            }

            var pairs = new List<(DiffItem Added, DiffItem Removed, bool SameName)>();
            var taken = new HashSet<DiffItem>(ReferenceEqualityComparer.Instance);

            // First by name (folder renamed, file moved), then the rest by size and time alone (file renamed).
            foreach (var byName in new[] { true, false })
            {
                var removedGroups = Group(removed.Where(i => !taken.Contains(i)), i => i.Target!, byName);
                var addedGroups = Group(added.Where(i => !taken.Contains(i)), i => i.Source!, byName);
                foreach (var (key, newItems) in addedGroups)
                {
                    // Many files of one size (and name) are too ambiguous to pair; they are copied as before.
                    if (!removedGroups.TryGetValue(key, out var oldItems) || newItems.Count > MaxGroup || oldItems.Count > MaxGroup)
                    {
                        continue;
                    }

                    foreach (var newItem in newItems)
                    {
                        var candidates = oldItems.Where(r => !taken.Contains(r) && TimesEqual(newItem.Source!, r.Target!)).Take(2).ToList();
                        if (candidates.Count != 1)
                        {
                            continue;
                        }

                        var old = candidates[0];

                        // Paths that differ only in letter case or Unicode form (a case-sensitive backup) may be the same
                        // file on the target's file system; those are removed and copied as before.
                        if (string.Equals(
                                PathKey.Normalize(newItem.Source!.RelativePath),
                                PathKey.Normalize(old.Target!.RelativePath),
                                StringComparison.OrdinalIgnoreCase)
                            || newItems.Count(a => !taken.Contains(a) && TimesEqual(a.Source!, old.Target!)) != 1)
                        {
                            continue;
                        }

                        taken.Add(newItem);
                        taken.Add(old);
                        pairs.Add((newItem, old, byName));
                    }
                }
            }

            if (pairs.Count == 0)
            {
                return;
            }

            var toVerify = pairs.Where(p => !p.SameName || options.CompareMode == CompareMode.Content).ToList();
            var stopwatch = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var verified = 0;
            var moves = new Dictionary<DiffItem, DiffItem>(ReferenceEqualityComparer.Instance);
            var replaced = new HashSet<DiffItem>(ReferenceEqualityComparer.Instance);
            foreach (var (newItem, old, sameName) in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sameName || options.CompareMode == CompareMode.Content)
                {
                    bool equal;
                    try
                    {
                        equal = ContentComparer.AreEqual(
                            fileSystem,
                            PathKey.ToSystemPath(sourceRoot, newItem.Source!.RelativePath),
                            PathKey.ToSystemPath(targetRoot, old.Target!.RelativePath),
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Unreadable now: copying and removing it as planned reports the problem if there is one.
                        equal = false;
                    }

                    verified++;
                    if (progress is not null && stopwatch.Elapsed - lastReport >= ProgressInterval)
                    {
                        lastReport = stopwatch.Elapsed;
                        progress.Report(new AnalysisProgress(
                            AnalysisStage.Comparing, source.Entries.Count, target.Entries.Count, verified, toVerify.Count));
                    }

                    if (!equal)
                    {
                        continue;
                    }
                }

                moves[newItem] = Item(DiffCategory.Moved, newItem.Source, old.Target);
                replaced.Add(old);
            }

            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (moves.TryGetValue(Items[i], out var moved))
                {
                    Items[i] = moved;
                }
                else if (replaced.Contains(Items[i]))
                {
                    Items.RemoveAt(i);
                }
            }
        }

        /// <summary>Files by size, and by name too when <paramref name="byName"/> (by the job's letter-case rule and Unicode form).</summary>
        private Dictionary<(long Length, string Name), List<DiffItem>> Group(
            IEnumerable<DiffItem> items, Func<DiffItem, ScannedEntry> entry, bool byName)
        {
            var groups = new Dictionary<(long Length, string Name), List<DiffItem>>(new SizeAndName(_comparer));
            foreach (var item in items)
            {
                var e = entry(item);
                var key = (e.Length, byName ? PathKey.Normalize(PathKey.GetName(e.RelativePath)) : string.Empty);
                if (!groups.TryGetValue(key, out var list))
                {
                    groups[key] = list = [];
                }

                list.Add(item);
            }

            return groups;
        }

        private sealed class SizeAndName(StringComparer names) : IEqualityComparer<(long Length, string Name)>
        {
            public bool Equals((long Length, string Name) x, (long Length, string Name) y) => x.Length == y.Length && names.Equals(x.Name, y.Name);

            public int GetHashCode((long Length, string Name) obj) => HashCode.Combine(obj.Length, names.GetHashCode(obj.Name));
        }

        // Within the tolerance, or exactly one hour apart on FAT (local time stored, daylight-saving change).
        private bool TimesEqual(ScannedEntry source, ScannedEntry target)
        {
            var delta = (source.LastWriteTimeUtc - target.LastWriteTimeUtc).Duration();
            return delta <= options.TimeTolerance
                || (ignoreHourShift && source.Length == target.Length && (delta - OneHour).Duration() <= options.TimeTolerance);
        }

        private void CompareContents(
            List<(ScannedEntry Source, ScannedEntry Target, bool Renamed)> pairs,
            ScanResult source,
            ScanResult target,
            IProgress<AnalysisProgress>? progress,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            for (var i = 0; i < pairs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (sourceEntry, targetEntry, renamed) = pairs[i];

                bool equal;
                try
                {
                    equal = ContentComparer.AreEqual(
                        fileSystem,
                        PathKey.ToSystemPath(sourceRoot, sourceEntry.RelativePath),
                        PathKey.ToSystemPath(targetRoot, targetEntry.RelativePath),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Items.Add(Item(DiffCategory.Error, sourceEntry, targetEntry) with { Message = ex.Message });
                    continue;
                }

                AddFileResult(sourceEntry, targetEntry, renamed, equal);

                if (progress is not null && stopwatch.Elapsed - lastReport >= ProgressInterval)
                {
                    lastReport = stopwatch.Elapsed;
                    progress.Report(new AnalysisProgress(
                        AnalysisStage.Comparing, source.Entries.Count, target.Entries.Count, i + 1, pairs.Count));
                }
            }
        }

        /// <param name="contentEqual">Result of the content compare, or null when contents were not compared.</param>
        private void AddFileResult(ScannedEntry sourceEntry, ScannedEntry targetEntry, bool renamed, bool? contentEqual)
        {
            var delta = sourceEntry.LastWriteTimeUtc - targetEntry.LastWriteTimeUtc;
            var timeEqual = TimesEqual(sourceEntry, targetEntry);
            var targetIsNewer = !timeEqual && delta < TimeSpan.Zero;
            var changedCategory = targetIsNewer ? DiffCategory.TargetNewer : DiffCategory.Modified;

            DiffCategory? category = contentEqual switch
            {
                true => timeEqual ? null : DiffCategory.TimestampOnly,
                false => changedCategory,
                null => sourceEntry.Length == targetEntry.Length && timeEqual ? null : changedCategory,
            };

            if (category is null && renamed)
            {
                category = DiffCategory.CaseRename;
            }

            if (category is null)
            {
                UnchangedFiles++;
                return;
            }

            Items.Add(Item(category.Value, sourceEntry, targetEntry));
        }

        private Dictionary<string, ScannedEntry> BuildMap(ScanResult scan, DiffSide side, HashSet<string> collisions)
        {
            const string Message =
                "Another entry in the same folder has a name that differs only in letter case or Unicode form; neither is changed on the target.";
            var map = new Dictionary<string, ScannedEntry>(scan.Entries.Count, _comparer);
            foreach (var entry in scan.Entries)
            {
                var key = PathKey.Normalize(entry.RelativePath);
                if (map.TryAdd(key, entry))
                {
                    continue;
                }

                collisions.Add(key);
                if (!Errors.Any(e => e.Side == side && e.RelativePath == map[key].RelativePath))
                {
                    Errors.Add(new DiffIssue(side, map[key].RelativePath, Message));
                }

                Errors.Add(new DiffIssue(side, entry.RelativePath, Message));
            }

            return map;
        }

        private HashSet<string> UnknownPaths(ScanResult scan) =>
            new(scan.Errors.Concat(scan.Skipped).Select(i => PathKey.Normalize(i.RelativePath)), _comparer);

        private void AddIssues(ScanResult scan, DiffSide side)
        {
            Errors.AddRange(scan.Errors.Select(i => new DiffIssue(side, i.RelativePath, i.Message)));
            Skipped.AddRange(scan.Skipped.Select(i => new DiffIssue(side, i.RelativePath, i.Message)));
        }

        // Matched names that are not written the same way are renamed on the target: a case difference, and on Windows
        // and Linux also a Unicode-form difference (NFC from Windows vs NFD from a Mac), because those file systems keep
        // both forms as different names, so files would otherwise land in a second folder. macOS treats both forms
        // as one name, so there it is not a rename.
        private static bool NamesEqual(ScannedEntry source, ScannedEntry target)
        {
            var sourceName = PathKey.GetName(source.RelativePath);
            var targetName = PathKey.GetName(target.RelativePath);
            return OperatingSystem.IsMacOS()
                ? string.Equals(PathKey.Normalize(sourceName), PathKey.Normalize(targetName), StringComparison.Ordinal)
                : string.Equals(sourceName, targetName, StringComparison.Ordinal);
        }

        private static DiffItem Item(DiffCategory category, ScannedEntry? source, ScannedEntry? target) => new()
        {
            RelativePath = (source ?? target)!.RelativePath,
            Kind = (source ?? target)!.Kind,
            Category = category,
            Source = source,
            Target = target,
        };
    }

    /// <summary>Combines progress of the two parallel scans into one <see cref="AnalysisProgress"/>.</summary>
    private sealed class ScanProgressTracker(IProgress<AnalysisProgress>? outer)
    {
        private int _source;
        private int _target;

        public IProgress<ScanProgress>? Source => outer is null ? null : new Relay(this, isSource: true);

        public IProgress<ScanProgress>? Target => outer is null ? null : new Relay(this, isSource: false);

        private void Update(bool isSource, int entries)
        {
            if (isSource)
            {
                Volatile.Write(ref _source, entries);
            }
            else
            {
                Volatile.Write(ref _target, entries);
            }

            outer!.Report(new AnalysisProgress(
                AnalysisStage.Scanning, Volatile.Read(ref _source), Volatile.Read(ref _target), 0, 0));
        }

        private sealed class Relay(ScanProgressTracker tracker, bool isSource) : IProgress<ScanProgress>
        {
            public void Report(ScanProgress value) => tracker.Update(isSource, value.EntriesFound);
        }
    }
}
