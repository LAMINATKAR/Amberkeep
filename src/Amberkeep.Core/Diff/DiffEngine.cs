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

        var exclusions = new ExclusionMatcher(patterns, caseSensitive);
        var scanner = new Scanner(fileSystem);
        var tracker = new ScanProgressTracker(progress);

        // Source and target are usually on different disks, so scanning them in parallel saves time.
        var sourceScan = Task.Run(
            () => scanner.Scan(sourceRoot, exclusions, tracker.Source, cancellationToken), cancellationToken);
        var targetScan = Task.Run(
            () => scanner.Scan(targetRoot, exclusions, tracker.Target, cancellationToken), cancellationToken);
        var scans = await Task.WhenAll(sourceScan, targetScan).ConfigureAwait(false);
        var source = scans[0];
        var target = scans[1];

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
                    Items.Add(Item(DiffCategory.Removed, null, targetEntry));
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
            var timeEqual = delta.Duration() <= options.TimeTolerance
                || (ignoreHourShift
                    && sourceEntry.Length == targetEntry.Length
                    && (delta.Duration() - OneHour).Duration() <= options.TimeTolerance);
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
