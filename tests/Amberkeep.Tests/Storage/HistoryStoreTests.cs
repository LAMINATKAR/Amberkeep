using Amberkeep.Core.Execution;
using Amberkeep.Storage.History;

namespace Amberkeep.Tests.Storage;

public sealed class HistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Records_are_listed_newest_first_and_pruned_to_the_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            _fx.History.Add(Record(T0.AddHours(i)), limit: 3);
        }

        var records = _fx.History.List();

        Assert.Equal([T0.AddHours(4), T0.AddHours(3), T0.AddHours(2)], records.Select(r => r.StartedUtc));
        Assert.Equal(3, Directory.GetFiles(_fx.History.Directory, "*.changes.json").Length);
        Assert.Equal(6, Directory.GetFiles(_fx.History.Directory).Length);
    }

    [Fact]
    public void Latest_record_per_job_and_target()
    {
        var job = Guid.NewGuid();
        var target = Guid.NewGuid();
        _fx.History.Add(Record(T0, job, target), 100);
        _fx.History.Add(Record(T0.AddDays(1), job, target, ExecutionOutcome.CompletedWithErrors), 100);
        _fx.History.Add(Record(T0.AddDays(2)), 100);

        var latest = _fx.History.LatestFor(job, target);

        Assert.Equal(ExecutionOutcome.CompletedWithErrors, latest!.Outcome);
        Assert.Null(_fx.History.LatestFor(Guid.NewGuid(), target));
    }

    [Fact]
    public void Damaged_record_is_skipped()
    {
        _fx.History.Add(Record(T0), 100);
        File.WriteAllText(Path.Combine(_fx.History.Directory, "20260101-000000-broken00.json"), "{ broken");

        Assert.Single(_fx.History.List());
    }

    [Fact]
    public void Records_of_a_job_can_be_deleted()
    {
        var job = Guid.NewGuid();
        _fx.History.Add(Record(T0, job), 100);
        _fx.History.Add(Record(T0.AddHours(1), job), 100);
        _fx.History.Add(Record(T0.AddHours(2)), 100);

        Assert.Equal(2, _fx.History.DeleteForJob(job));
        Assert.Single(_fx.History.List());
    }

    [Fact]
    public void Missing_history_folder_is_empty()
    {
        Assert.Empty(_fx.History.List());
    }

    private static RunRecord Record(
        DateTimeOffset started, Guid? job = null, Guid? target = null, ExecutionOutcome outcome = ExecutionOutcome.Completed) => new()
    {
        RunId = Guid.NewGuid(),
        JobId = job ?? Guid.NewGuid(),
        TargetId = target ?? Guid.NewGuid(),
        JobName = "Job",
        StartedUtc = started,
        FinishedUtc = started.AddMinutes(1),
        Outcome = outcome,
        Failures = [new ExecutionFailure("a.txt", ExecutionOperation.Copy, "Locked")],
    };
}
