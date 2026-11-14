using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.History;
using Amberkeep.Storage.Targets;

namespace Amberkeep.Storage.Running;

/// <summary>Result of the analysis step of a job run.</summary>
/// <param name="Job">The job.</param>
/// <param name="Target">The target, with <see cref="TargetConfig.LastKnownPath"/> updated when it was found elsewhere.</param>
/// <param name="Status">Where the target was found; a diff exists only when it is connected.</param>
/// <param name="Diff">The diff, or null when the target is not connected.</param>
public sealed record PreparedRun(JobConfig Job, TargetConfig Target, TargetStatus Status, DiffResult? Diff)
{
    /// <summary>The target was found at a different path than last time; the caller should save <see cref="Target"/>.</summary>
    public bool TargetMoved { get; init; }
}

/// <summary>Runs a job against one target: locate → analyze → (approval by the caller) → execute → history.</summary>
public sealed class JobRunner(
    IWritableFileSystem fileSystem,
    IVolumeProvider volumes,
    HistoryStore history,
    TimeProvider timeProvider)
{
    private readonly TargetLocator _locator = new(fileSystem, volumes);

    /// <summary>Number of run records kept; taken from the settings by the caller.</summary>
    public int HistoryLimit { get; set; } = new AppSettings().HistoryLimit;

    /// <summary>Amberkeep's own folder; never part of a target, skipped inside a source (DiffOptions.ProtectedFolders).</summary>
    public string? ProgramDirectory { get; init; }

    /// <summary>The configuration as saved now; used to check a target that was found at a new place.</summary>
    public Func<AppConfig>? CurrentConfig { get; init; }

    public async Task<PreparedRun> AnalyzeAsync(
        JobConfig job,
        TargetConfig target,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(target);
        if (!Path.IsPathFullyQualified(job.SourcePath))
        {
            // Only a hand-edited file can get here; the editor does not save such a job.
            throw new InvalidOperationException($"The source folder of '{job.Name}' is missing or not a full path. Edit the backup.");
        }

        var status = _locator.Locate(target);
        if (!status.IsConnected)
        {
            return new PreparedRun(job, target, status, null);
        }

        var moved = !string.Equals(status.Path, target.LastKnownPath, StringComparison.Ordinal);

        // A target found somewhere new (another drive letter, a copied backup folder) must pass the same checks as
        // when it was added: not in or around another backup's source or target, nor around Amberkeep itself.
        if (moved && CurrentConfig?.Invoke() is { } config
            && config.FindPathConflict(status.Path!, target.Id, job, ProgramDirectory, onDisk: true) is { } conflict)
        {
            return new PreparedRun(job, target, TargetStatus.Unverified(status.Path!, $"Found at {status.Path}, but it cannot be used there: {conflict}"), null);
        }

        var located = moved ? new TargetRegistry(fileSystem, volumes, timeProvider).WithLocation(target, status.Path!) : target;
        var diff = await new DiffEngine(fileSystem)
            .AnalyzeAsync(new DiffRequest(job.SourcePath, status.Path!, job.ToDiffOptions() with { ProtectedFolders = ProgramDirectory is null ? [] : [ProgramDirectory] }), progress, cancellationToken)
            .ConfigureAwait(false);

        return new PreparedRun(job, located, status, diff) { TargetMoved = moved };
    }

    /// <param name="run">Result of <see cref="AnalyzeAsync"/> with a connected target.</param>
    /// <param name="selection">Items the user selected.</param>
    /// <param name="approved">The user reviewed and approved the diff.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancels the run safely.</param>
    public async Task<RunRecord> ExecuteAsync(
        PreparedRun run,
        IReadOnlySet<DiffItem> selection,
        bool approved,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(selection);
        if (run.Diff is null || run.Status.Path is null)
        {
            throw new InvalidOperationException("The target is not connected; there is nothing to execute.");
        }

        var started = timeProvider.GetUtcNow();
        var selected = run.Diff.Items.Where(selection.Contains).ToList();

        ExecutionResult result;
        if (run.Job.RequireApproval && !approved)
        {
            result = Refused("This backup requires the changes to be reviewed and approved.");
        }
        else if (!MarkerFile.BelongsTo(fileSystem, run.Status.Path, run.Target.Id))
        {
            // The disk may have been swapped since the analysis.
            result = Refused("The target could not be verified anymore (its Amberkeep marker is missing or different). Analyze again.");
        }
        else
        {
            result = await new MirrorExecutor(fileSystem, timeProvider)
                .ExecuteAsync(run.Diff, selection, run.Job.ToExecutionOptions(approved), progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var record = RunRecord.Create(
            Guid.NewGuid(),
            run.Job.Id,
            run.Job.Name,
            run.Target.Id,
            run.Target.Name,
            run.Diff,
            selected,
            result,
            started,
            timeProvider.GetUtcNow());

        try
        {
            history.Add(record, HistoryLimit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            record = record with { Notes = [.. record.Notes, $"The run could not be saved to the history: {ex.Message}"] };
        }

        return record;
    }

    private static ExecutionResult Refused(string reason) =>
        new() { Outcome = ExecutionOutcome.Refused, RefusedReason = reason };
}
