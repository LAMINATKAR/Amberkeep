using System.Globalization;
using Amberkeep.Core.Paths;

namespace Amberkeep.Core.Safety;

/// <summary>Individual safety checks (SPEC 4.5). Pure functions so the UI can re-evaluate them after selection changes.</summary>
public static class SafetyChecks
{
    public static SafetyIssue SourceMissing(string sourcePath) => new(
        SafetyIssueKind.SourceMissing,
        SafetySeverity.Blocking,
        $"Source folder does not exist or is not available: {sourcePath}");

    public static SafetyIssue SourceUnreadable(string sourcePath, string error) => new(
        SafetyIssueKind.SourceUnreadable,
        SafetySeverity.Blocking,
        $"Source folder cannot be read: {sourcePath} ({error})");

    public static SafetyIssue TargetUnreadable(string targetPath, string error) => new(
        SafetyIssueKind.TargetUnreadable,
        SafetySeverity.Blocking,
        $"Target folder cannot be read: {targetPath} ({error})");

    public static SafetyIssue TargetIsFile(string targetPath) => new(
        SafetyIssueKind.TargetIsFile,
        SafetySeverity.Blocking,
        $"Target path is a file, not a folder: {targetPath}");

    public static SafetyIssue TargetMissing(string targetPath) => new(
        SafetyIssueKind.TargetMissing,
        SafetySeverity.Info,
        $"Target folder does not exist yet and will be created: {targetPath}");

    /// <remarks>
    /// Uses the platform's case rules, not the job's name-matching option: the question is whether two paths
    /// point to the same place on disk.
    /// </remarks>
    /// <param name="onDisk">Also compare where the folders really are (junctions, links, drive aliases, shares).</param>
    public static SafetyIssue? CheckOverlap(string sourcePath, string targetPath, bool onDisk = false)
    {
        var caseSensitive = CaseSensitivity.Auto.IsCaseSensitive();
        var message = PathOverlap.Check(sourcePath, targetPath, caseSensitive, onDisk) switch
        {
            PathRelation.Same => "Source and target are the same folder.",
            PathRelation.TargetInsideSource => "Target folder is inside the source folder.",
            PathRelation.SourceInsideTarget => "Source folder is inside the target folder.",
            _ => null,
        };

        return message is null ? null : new SafetyIssue(SafetyIssueKind.PathsOverlap, SafetySeverity.Blocking, message);
    }

    /// <summary>
    /// Amberkeep's own folder (its settings and history) must never be part of a target: a mirror would remove it.
    /// Inside a source it is skipped, because Amberkeep writes there while it runs.
    /// </summary>
    /// <returns>A blocking issue, or an info issue with the exclusion pattern that skips the folder, or nothing.</returns>
    public static (SafetyIssue? Issue, string? Exclusion) CheckProgramFolder(string programFolder, string sourcePath, string targetPath)
    {
        var caseSensitive = CaseSensitivity.Auto.IsCaseSensitive();
        if (PathOverlap.Check(programFolder, targetPath, caseSensitive, onDisk: true) != PathRelation.Separate)
        {
            return (new SafetyIssue(
                SafetyIssueKind.ProgramFolder,
                SafetySeverity.Blocking,
                $"The target overlaps the folder of Amberkeep itself ({programFolder}). A mirror would remove its settings and history. Choose another target or move Amberkeep."), null);
        }

        switch (PathOverlap.Check(sourcePath, programFolder, caseSensitive, onDisk: true))
        {
            case PathRelation.Same:
            case PathRelation.SourceInsideTarget:
                return (new SafetyIssue(
                    SafetyIssueKind.ProgramFolder,
                    SafetySeverity.Blocking,
                    $"The source is the folder of Amberkeep itself or inside it ({programFolder}). Move Amberkeep to another folder."), null);
            case PathRelation.TargetInsideSource:
                var relative = RelativeInside(sourcePath, programFolder);
                return relative is null
                    ? (new SafetyIssue(
                        SafetyIssueKind.ProgramFolder,
                        SafetySeverity.Blocking,
                        $"The folder of Amberkeep itself ({programFolder}) is inside the source through a link and cannot be skipped. Move Amberkeep to another folder."), null)
                    : (new SafetyIssue(
                        SafetyIssueKind.ProgramFolder,
                        SafetySeverity.Info,
                        $"The folder of Amberkeep itself ({relative}) is inside the source. It is skipped: not copied, and not removed on the target."),
                       "/" + relative.Replace('\\', PathKey.Separator).TrimEnd(PathKey.Separator) + PathKey.Separator);
            default:
                return (null, null);
        }
    }

    // The path of a folder inside the source, relative to the source; by the paths as written or as they are on disk.
    private static string? RelativeInside(string sourcePath, string folder)
    {
        foreach (var (root, inner) in new[]
                 {
                     (Path.GetFullPath(sourcePath), Path.GetFullPath(folder)),
                     (PathIdentity.TryGetRealPath(sourcePath) ?? string.Empty, PathIdentity.TryGetRealPath(folder) ?? string.Empty),
                 })
        {
            if (root.Length == 0 || inner.Length == 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, inner);
            if (relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return relative;
            }
        }

        return null;
    }

    /// <summary>
    /// An empty source next to a non-empty target usually means a missing or unmounted source,
    /// and mirroring it would wipe the backup.
    /// </summary>
    public static SafetyIssue? CheckSourceEmpty(int sourceFileCount, int targetFileCount) =>
        sourceFileCount == 0 && targetFileCount > 0
            ? new SafetyIssue(
                SafetyIssueKind.SourceEmpty,
                SafetySeverity.Blocking,
                $"Source folder contains no files, but the target contains {targetFileCount}. The run is blocked to protect the backup.")
            : null;

    public static SafetyIssue? CheckMassRemoval(int removedFileCount, int targetFileCount, MassRemovalGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentOutOfRangeException.ThrowIfNegative(removedFileCount);
        ArgumentOutOfRangeException.ThrowIfNegative(targetFileCount);
        guard.Validate();

        if (!guard.Enabled || targetFileCount == 0 || removedFileCount == 0)
        {
            return null;
        }

        // Either limit asks: a large share of a folder, or simply many files.
        var overShare = guard.Percent < 100
            && removedFileCount >= MassRemovalGuard.PercentFloor
            && (long)removedFileCount * 100 > (long)guard.Percent * targetFileCount;
        var overCount = guard.Count > 0 && removedFileCount >= guard.Count;
        if (!overShare && !overCount)
        {
            return null;
        }

        var share = (double)removedFileCount / targetFileCount;
        var reason = overShare
            ? string.Create(CultureInfo.InvariantCulture, $"more than {guard.Percent} % of the files")
            : string.Create(CultureInfo.InvariantCulture, $"at least {guard.Count:N0} files");
        return new SafetyIssue(
            SafetyIssueKind.MassRemoval,
            SafetySeverity.RequiresApproval,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{removedFileCount:N0} of {targetFileCount:N0} files on the target ({share:P0}) would be removed – {reason}."));
    }
}
