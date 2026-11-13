using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Storage.History;
using Amberkeep.Storage.Targets;

namespace Amberkeep.Tests.Storage;

public sealed class JobRunnerTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Analyze_and_execute_mirror_the_job_and_record_history()
    {
        var (job, target) = _fx.JobWithTarget();
        _fx.Source.File("a.txt", "a").File("folder/b.txt", "b");

        var prepared = await _fx.Runner.AnalyzeAsync(job, target, cancellationToken: Ct);
        var record = await _fx.Runner.ExecuteAsync(prepared, DefaultSelection(prepared.Diff!), approved: true, cancellationToken: Ct);

        Assert.Equal(ExecutionOutcome.Completed, record.Outcome);
        Assert.Equal(2, record.CopiedFiles);
        Assert.Equal(job.Id, record.JobId);
        Assert.Equal(target.Id, record.TargetId);
        Assert.Equal(3, record.SelectedChanges.Count);
        Assert.Equal(record.RunId, Assert.Single(_fx.History.List()).RunId);
        Assert.Equal("b", File.ReadAllText(Path.Combine(target.LastKnownPath, "folder", "b.txt")));

        var again = await _fx.Runner.AnalyzeAsync(job, target, cancellationToken: Ct);
        Assert.Empty(again.Diff!.Items);
    }

    [Fact]
    public async Task Marker_is_kept_by_the_mirror()
    {
        var (job, target) = _fx.JobWithTarget();
        _fx.Source.File("a.txt");

        var prepared = await _fx.Runner.AnalyzeAsync(job, target, cancellationToken: Ct);
        await _fx.Runner.ExecuteAsync(prepared, DefaultSelection(prepared.Diff!), approved: true, cancellationToken: Ct);

        Assert.Equal(TargetState.Connected, _fx.Locator.Locate(target).State);
    }

    [Fact]
    public async Task Swapped_disk_between_analysis_and_execution_is_refused()
    {
        var (job, target) = _fx.JobWithTarget();
        _fx.Source.File("a.txt");
        var prepared = await _fx.Runner.AnalyzeAsync(job, target, cancellationToken: Ct);
        File.Delete(Path.Combine(target.LastKnownPath, ".amberkeep", "target.json"));

        var record = await _fx.Runner.ExecuteAsync(prepared, DefaultSelection(prepared.Diff!), approved: true, cancellationToken: Ct);

        Assert.Equal(ExecutionOutcome.Refused, record.Outcome);
        Assert.False(File.Exists(Path.Combine(target.LastKnownPath, "a.txt")));
        Assert.Single(_fx.History.List());
    }

    [Fact]
    public async Task Job_requiring_approval_is_refused_without_it()
    {
        var (job, target) = _fx.JobWithTarget();
        _fx.Source.File("a.txt");
        var prepared = await _fx.Runner.AnalyzeAsync(job, target, cancellationToken: Ct);

        var record = await _fx.Runner.ExecuteAsync(prepared, DefaultSelection(prepared.Diff!), approved: false, cancellationToken: Ct);

        Assert.Equal(ExecutionOutcome.Refused, record.Outcome);
        Assert.False(File.Exists(Path.Combine(target.LastKnownPath, "a.txt")));
    }

    [Fact]
    public async Task Job_without_approval_requirement_runs_unattended()
    {
        var (job, target) = _fx.JobWithTarget();
        job = job with { RequireApproval = false };
        _fx.Source.File("a.txt");
        var prepared = await _fx.Runner.AnalyzeAsync(job, target, cancellationToken: Ct);

        var record = await _fx.Runner.ExecuteAsync(prepared, DefaultSelection(prepared.Diff!), approved: false, cancellationToken: Ct);

        Assert.Equal(ExecutionOutcome.Completed, record.Outcome);
    }

    [Fact]
    public async Task Disconnected_target_gives_no_diff()
    {
        var (job, target) = _fx.JobWithTarget();
        _fx.Volumes.Roots.Clear();
        Directory.Delete(Path.Combine(_fx.VolumeA, "Backup"), recursive: true);

        var prepared = await _fx.Runner.AnalyzeAsync(job, target, cancellationToken: Ct);

        Assert.Equal(TargetState.NotConnected, prepared.Status.State);
        Assert.Null(prepared.Diff);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fx.Runner.ExecuteAsync(prepared, new HashSet<DiffItem>(), approved: true, cancellationToken: Ct));
    }

    [Fact]
    public async Task Target_found_on_another_volume_reports_its_new_location()
    {
        var (job, target) = _fx.JobWithTarget();
        Directory.Move(Path.Combine(_fx.VolumeA, "Backup"), Path.Combine(_fx.VolumeB, "Backup"));

        var prepared = await _fx.Runner.AnalyzeAsync(job, target, cancellationToken: Ct);

        Assert.True(prepared.TargetMoved);
        Assert.Equal(Path.Combine(_fx.VolumeB, "Backup", "Photos"), prepared.Target.LastKnownPath);
        Assert.NotNull(prepared.Diff);
    }

    [Fact]
    public void Record_keeps_at_most_ten_thousand_changes()
    {
        var items = Enumerable.Range(0, RunRecord.MaxRecordedChanges + 5)
            .Select(i => new DiffItem
            {
                RelativePath = $"f{i}.txt",
                Kind = Core.FileSystem.EntryKind.File,
                Category = DiffCategory.New,
                Source = new Core.Scanning.ScannedEntry($"f{i}.txt", Core.FileSystem.EntryKind.File, 1, DateTime.UtcNow),
            })
            .ToList();
        var diff = new DiffResult
        {
            SourcePath = "s",
            TargetPath = "t",
            Options = DiffOptions.Default,
            Items = items,
            Summary = DiffSummary.Compute(items, _ => true, 0),
        };

        var record = RunRecord.Create(
            Guid.NewGuid(), Guid.NewGuid(), "Job", Guid.NewGuid(), "Target", diff, items,
            new ExecutionResult { Outcome = ExecutionOutcome.Completed }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.Equal(RunRecord.MaxRecordedChanges, record.SelectedChanges.Count);
        Assert.True(record.ChangesTruncated);
        Assert.Equal(RunRecord.MaxRecordedChanges + 5, Assert.Single(record.DiffCounts).Files);
    }

    private static HashSet<DiffItem> DefaultSelection(DiffResult diff) => diff.Items.Where(i => i.DefaultSelected).ToHashSet();
}
