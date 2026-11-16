using Amberkeep.Core.Safety;

namespace Amberkeep.Core.Diff;

public sealed class DiffResult
{
    public required string SourcePath { get; init; }

    public required string TargetPath { get; init; }

    /// <summary>Options the diff was made with.</summary>
    public required DiffOptions Options { get; init; }

    /// <summary>Proposed changes, ordered so that each directory is followed by its contents.</summary>
    public IReadOnlyList<DiffItem> Items { get; init; } = [];

    /// <summary>Paths that could not be read. Nothing under them is changed on the target.</summary>
    public IReadOnlyList<DiffIssue> Errors { get; init; } = [];

    /// <summary>Links that were not followed, and names Windows cannot handle reliably. Nothing under them is changed on the target.</summary>
    public IReadOnlyList<DiffIssue> Skipped { get; init; } = [];

    public IReadOnlyList<SafetyIssue> SafetyIssues { get; init; } = [];

    public required DiffSummary Summary { get; init; }

    public int SourceFileCount { get; init; }

    public int TargetFileCount { get; init; }

    public bool IsBlocked => SafetyIssues.Any(i => i.Severity == SafetySeverity.Blocking);

    public bool RequiresApproval => SafetyIssues.Any(i => i.Severity == SafetySeverity.RequiresApproval);
}

public enum AnalysisStage
{
    Scanning,
    Comparing,
    Done,
}

public sealed record AnalysisProgress(
    AnalysisStage Stage,
    int SourceEntries,
    int TargetEntries,
    int ComparedFiles,
    int FilesToCompare);
