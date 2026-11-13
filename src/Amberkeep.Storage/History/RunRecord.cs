using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Core.FileSystem;

namespace Amberkeep.Storage.History;

public sealed record CategoryCount(DiffCategory Category, int Files, int Directories, long Bytes);

public sealed record RecordedChange(string Path, EntryKind Kind, DiffCategory Category);

/// <summary>One run of a job against one target, stored in <c>history/</c>.</summary>
public sealed record RunRecord
{
    public const int CurrentFormat = 1;

    /// <summary>At most this many changes are stored per run; the rest is only counted.</summary>
    public const int MaxRecordedChanges = 10_000;

    public int Format { get; init; } = CurrentFormat;

    public Guid RunId { get; init; }

    public Guid JobId { get; init; }

    public string JobName { get; init; } = string.Empty;

    public Guid TargetId { get; init; }

    public string TargetName { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public string TargetPath { get; init; } = string.Empty;

    public DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset FinishedUtc { get; init; }

    public ExecutionOutcome Outcome { get; init; }

    public string? RefusedReason { get; init; }

    public int CopiedFiles { get; init; }

    public int ReplacedFiles { get; init; }

    public int RemovedFiles { get; init; }

    public int RemovedDirectories { get; init; }

    public int CreatedDirectories { get; init; }

    public int RenamedEntries { get; init; }

    public int UpdatedTimestamps { get; init; }

    public int ArchivedFiles { get; init; }

    public long CopiedBytes { get; init; }

    public string? ArchiveFolder { get; init; }

    /// <summary>Counts of the whole diff per category (selected or not).</summary>
    public IReadOnlyList<CategoryCount> DiffCounts { get; init; } = [];

    public IReadOnlyList<ExecutionFailure> Failures { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Changes selected for this run (whether or not each one succeeded – see <see cref="Failures"/>).</summary>
    /// <remarks>
    /// Stored in a separate <c>.changes.json</c> file so that listing the history stays cheap;
    /// load with <see cref="HistoryStore.ReadChanges"/>.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<RecordedChange> SelectedChanges { get; init; } = [];

    public bool ChangesTruncated { get; init; }

    public static RunRecord Create(
        Guid runId,
        Guid jobId,
        string jobName,
        Guid targetId,
        string targetName,
        DiffResult diff,
        IReadOnlyCollection<DiffItem> selected,
        ExecutionResult result,
        DateTimeOffset startedUtc,
        DateTimeOffset finishedUtc)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(result);

        return new RunRecord
        {
            RunId = runId,
            JobId = jobId,
            JobName = jobName,
            TargetId = targetId,
            TargetName = targetName,
            SourcePath = diff.SourcePath,
            TargetPath = diff.TargetPath,
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            Outcome = result.Outcome,
            RefusedReason = result.RefusedReason,
            CopiedFiles = result.CopiedFiles,
            ReplacedFiles = result.ReplacedFiles,
            RemovedFiles = result.RemovedFiles,
            RemovedDirectories = result.RemovedDirectories,
            CreatedDirectories = result.CreatedDirectories,
            RenamedEntries = result.RenamedEntries,
            UpdatedTimestamps = result.UpdatedTimestamps,
            ArchivedFiles = result.ArchivedFiles,
            CopiedBytes = result.CopiedBytes,
            ArchiveFolder = result.ArchiveFolder,
            DiffCounts = diff.Summary.ByCategory
                .OrderBy(p => p.Key)
                .Select(p => new CategoryCount(p.Key, p.Value.Files, p.Value.Directories, p.Value.Bytes))
                .ToList(),
            Failures = result.Failures,
            Notes = result.Notes,
            SelectedChanges = selected
                .Take(MaxRecordedChanges)
                .Select(i => new RecordedChange(i.RelativePath, i.Kind, i.Category))
                .ToList(),
            ChangesTruncated = selected.Count > MaxRecordedChanges,
        };
    }
}
