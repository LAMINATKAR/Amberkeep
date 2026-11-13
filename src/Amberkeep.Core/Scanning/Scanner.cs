using System.Diagnostics;
using Amberkeep.Core.Exclusions;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Paths;

namespace Amberkeep.Core.Scanning;

/// <summary>Recursively lists a folder. Read-only.</summary>
public sealed class Scanner(IFileSystem fileSystem)
{
    /// <summary>Folder in the root of a target that holds program-owned files. Never compared or mirrored.</summary>
    public const string ProgramFolderName = ".amberkeep";

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    public ScanResult Scan(
        string rootPath,
        ExclusionMatcher exclusions,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        ArgumentNullException.ThrowIfNull(exclusions);

        if (!fileSystem.DirectoryExists(rootPath))
        {
            return new ScanResult { RootPath = rootPath, RootExists = false };
        }

        var entries = new List<ScannedEntry>();
        var errors = new List<ScanIssue>();
        var skipped = new List<ScanIssue>();
        var pending = new Stack<(string FullPath, string RelativePath)>();
        pending.Push((rootPath, string.Empty));

        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (fullPath, relativePath) = pending.Pop();

            IReadOnlyList<FileSystemEntry> children;
            try
            {
                children = fileSystem.ListDirectory(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (relativePath.Length == 0)
                {
                    return new ScanResult { RootPath = rootPath, RootExists = true, RootError = ex.Message };
                }

                errors.Add(new ScanIssue(relativePath, ex.Message));
                continue;
            }

            foreach (var child in children)
            {
                if (relativePath.Length == 0
                    && string.Equals(child.Name, ProgramFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var childPath = PathKey.Combine(relativePath, child.Name);
                if (child.IsLink)
                {
                    skipped.Add(new ScanIssue(childPath, "Symbolic link or junction – not followed."));
                    continue;
                }

                var isDirectory = child.Kind == EntryKind.Directory;
                if (exclusions.IsExcluded(childPath, isDirectory))
                {
                    continue;
                }

                entries.Add(new ScannedEntry(childPath, child.Kind, child.Length, child.LastWriteTimeUtc));
                if (isDirectory)
                {
                    pending.Push((Path.Join(fullPath, child.Name), childPath));
                }
            }

            if (progress is not null && stopwatch.Elapsed - lastReport >= ProgressInterval)
            {
                lastReport = stopwatch.Elapsed;
                progress.Report(new ScanProgress(entries.Count, relativePath));
            }
        }

        progress?.Report(new ScanProgress(entries.Count, string.Empty));

        return new ScanResult
        {
            RootPath = rootPath,
            RootExists = true,
            Entries = entries,
            FileCount = entries.Count(e => e.Kind == EntryKind.File),
            Errors = errors,
            Skipped = skipped,
        };
    }
}
