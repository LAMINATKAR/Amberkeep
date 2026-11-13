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
    public static SafetyIssue? CheckOverlap(string sourcePath, string targetPath)
    {
        var caseSensitive = CaseSensitivity.Auto.IsCaseSensitive();
        var message = PathOverlap.Check(sourcePath, targetPath, caseSensitive) switch
        {
            PathRelation.Same => "Source and target are the same folder.",
            PathRelation.TargetInsideSource => "Target folder is inside the source folder.",
            PathRelation.SourceInsideTarget => "Source folder is inside the target folder.",
            _ => null,
        };

        return message is null ? null : new SafetyIssue(SafetyIssueKind.PathsOverlap, SafetySeverity.Blocking, message);
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

        if (!guard.Enabled || targetFileCount == 0 || removedFileCount < guard.MinCount)
        {
            return null;
        }

        if ((long)removedFileCount * 100 <= (long)guard.Percent * targetFileCount)
        {
            return null;
        }

        var share = (double)removedFileCount / targetFileCount;
        return new SafetyIssue(
            SafetyIssueKind.MassRemoval,
            SafetySeverity.RequiresApproval,
            $"{removedFileCount} of {targetFileCount} files on the target ({share:P0}) would be removed.");
    }
}
