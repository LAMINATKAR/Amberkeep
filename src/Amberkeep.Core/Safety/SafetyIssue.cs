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
}

public sealed record SafetyIssue(SafetyIssueKind Kind, SafetySeverity Severity, string Message);

/// <summary>
/// Requires approval when a run would remove many files from the target.
/// Triggers only when both limits are exceeded, so that normal clean-ups do not bother the user.
/// </summary>
/// <param name="Enabled">The guard can be switched off.</param>
/// <param name="Percent">Removed files must exceed this share of all files on the target (0–100).</param>
/// <param name="MinCount">…and there must be at least this many removed files.</param>
public sealed record MassRemovalGuard(bool Enabled = true, int Percent = 50, int MinCount = 100)
{
    /// <exception cref="ArgumentOutOfRangeException">Percent is outside 0–100 or MinCount is negative.</exception>
    public void Validate()
    {
        if (Percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Percent), Percent, "Percent must be between 0 and 100.");
        }

        if (MinCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinCount), MinCount, "MinCount must not be negative.");
        }
    }
}
