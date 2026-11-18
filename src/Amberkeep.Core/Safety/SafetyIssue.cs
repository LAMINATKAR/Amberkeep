namespace Amberkeep.Core.Safety;

public enum SafetySeverity
{
    /// <summary>For information only.</summary>
    Info,

    /// <summary>The run must be approved by the user, even when approval is otherwise switched off.</summary>
    RequiresApproval,

    /// <summary>The run must not be executed.</summary>
    Blocking,
}

public enum SafetyIssueKind
{
    SourceMissing,
    SourceUnreadable,
    SourceEmpty,
    PathsOverlap,
    TargetUnreadable,
    TargetIsFile,
    TargetMissing,
    MassRemoval,
    ProgramFolder,
    InvalidSelection,
}

public sealed record SafetyIssue(SafetyIssueKind Kind, SafetySeverity Severity, string Message);

/// <summary>
/// Requires approval when a run would remove many files from the target: more than <paramref name="Percent"/> % of
/// the target's files (and at least <see cref="PercentFloor"/> files) OR at least <paramref name="Count"/> files.
/// </summary>
/// <param name="Enabled">The guard can be switched off.</param>
/// <param name="Percent">Share of all files on the target (0–100) that removed files must exceed; 100 switches this limit off.</param>
/// <param name="Count">Number of removed files that always asks; 0 switches this limit off.</param>
public sealed record MassRemovalGuard(bool Enabled = true, int Percent = 50, int Count = 1000)
{
    /// <summary>The share limit applies only from this many removed files, so tidying a small folder never asks.</summary>
    public const int PercentFloor = 20;

    /// <exception cref="ArgumentOutOfRangeException">Percent is outside 0–100 or Count is negative.</exception>
    public void Validate()
    {
        if (Percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Percent), Percent, "Percent must be between 0 and 100.");
        }

        if (Count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Count), Count, "Count must not be negative.");
        }
    }
}
