using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Core.FileSystem;

namespace Amberkeep.Storage.History;

public sealed record CategoryCount(DiffCategory Category, int Files, int Directories, long Bytes);

/// <param name="From">For a moved file, its old path on the target.</param>
public sealed record RecordedChange(string Path, EntryKind Kind, DiffCategory Category, string? From = null);

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

    public int MovedFiles { get; init; }

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

    /// <summary>The record with missing values of a hand-edited or damaged file filled in, so that views and reports never meet a null.</summary>
    public RunRecord Normalized() => this with
    {
        JobName = JobName ?? string.Empty,
        TargetName = TargetName ?? string.Empty,
        SourcePath = SourcePath ?? string.Empty,
        TargetPath = TargetPath ?? string.Empty,
        DiffCounts = DiffCounts?.Where(c => c is not null).ToList() ?? [],
        Failures = Failures?
            .Where(f => f is not null)
            .Select(f => f with { RelativePath = f.RelativePath ?? string.Empty, Message = f.Message ?? string.Empty })
            .ToList() ?? [],
        Notes = Notes?.Where(n => n is not null).ToList() ?? [],
        SelectedChanges = NormalizedChanges(SelectedChanges),
    };

    /// <summary>Changes without null entries or paths (a damaged <c>.changes.json</c>).</summary>
    public static IReadOnlyList<RecordedChange> NormalizedChanges(IReadOnlyList<RecordedChange>? changes) =>
        changes?.Where(c => c is not null && c.Path is not null).ToList() ?? [];

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
            MovedFiles = result.MovedFiles,
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
                .Select(i => new RecordedChange(i.RelativePath, i.Kind, i.Category, i.Category == DiffCategory.Moved ? i.Target!.RelativePath : null))
                .ToList(),
            ChangesTruncated = selected.Count > MaxRecordedChanges,
        };
    }
}
