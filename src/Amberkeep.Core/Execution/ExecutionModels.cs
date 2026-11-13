namespace Amberkeep.Core.Execution;

public sealed record ExecutionOptions
{
    /// <summary>Move removed and overwritten files to the archive instead of deleting them.</summary>
    public bool ArchiveEnabled { get; init; }

    /// <summary>Archive snapshots older than this are deleted after the run; null keeps them forever.</summary>
    public TimeSpan? ArchiveRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The user approved the diff. Required when a safety check asks for approval.</summary>
    public bool Approved { get; init; }

    /// <summary>Compare every copied file with its source before it replaces anything.</summary>
    public bool VerifyCopies { get; init; }
}

public enum ExecutionOutcome
{
    Completed,
    CompletedWithErrors,
    Cancelled,

    /// <summary>Nothing was changed: the diff is blocked, needs approval, or there is not enough free space.</summary>
    Refused,
}

public enum ExecutionOperation
{
    Prepare,
    Remove,
    Rename,
    CreateDirectory,
    Copy,
    UpdateTimestamp,
    CleanArchive,
}

public sealed record ExecutionFailure(string RelativePath, ExecutionOperation Operation, string Message);

public sealed record ExecutionProgress(
    ExecutionOperation Operation,
    string RelativePath,
    int CompletedOperations,
    int TotalOperations,
    long CopiedBytes,
    long TotalBytes);

public sealed class ExecutionResult
{
    public required ExecutionOutcome Outcome { get; init; }

    public string? RefusedReason { get; init; }

    public int CopiedFiles { get; init; }

    public int ReplacedFiles { get; init; }

    public int RemovedFiles { get; init; }

    public int RemovedDirectories { get; init; }

    public int CreatedDirectories { get; init; }

    public int RenamedEntries { get; init; }

    public int UpdatedTimestamps { get; init; }

    /// <summary>Files moved to the archive (removed or overwritten).</summary>
    public int ArchivedFiles { get; init; }

    public long CopiedBytes { get; init; }

    public IReadOnlyList<ExecutionFailure> Failures { get; init; } = [];

    /// <summary>Things worth telling the user that are not errors (e.g. a folder kept because it is not empty).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Snapshot folder of this run in the archive, when anything was archived.</summary>
    public string? ArchiveFolder { get; init; }

    public TimeSpan Duration { get; init; }
}
