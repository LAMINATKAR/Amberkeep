using Amberkeep.Core.Diff;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.Targets;

namespace Amberkeep.Tests.Storage;

public sealed class CleanupTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Removing_a_target_deletes_only_its_marker_by_default()
    {
        var (job, target) = Backup(withArchive: true);
        var config = new AppConfig().WithJob(job);

        var (updated, report) = _fx.Cleanup.RemoveTarget(config, job.Id, target.Id, removeMarker: true, removeArchive: false);

        Assert.Empty(updated.Jobs.Single().Targets);
        Assert.Empty(_fx.Store.Load().Config!.Jobs.Single().Targets);
        Assert.Empty(report.Errors);
        Assert.False(File.Exists(Path.Combine(target.LastKnownPath, ".amberkeep", "target.json")));
        Assert.True(Directory.Exists(Path.Combine(target.LastKnownPath, ".amberkeep", "archive")));
        Assert.True(File.Exists(Path.Combine(target.LastKnownPath, "data.txt")));
    }

    [Fact]
    public void Archive_is_removed_only_when_asked_and_empty_program_folder_goes_too()
    {
        var (job, target) = Backup(withArchive: true);

        _fx.Cleanup.RemoveTarget(new AppConfig().WithJob(job), job.Id, target.Id, removeMarker: true, removeArchive: true);

        Assert.False(Directory.Exists(Path.Combine(target.LastKnownPath, ".amberkeep")));
        Assert.True(File.Exists(Path.Combine(target.LastKnownPath, "data.txt")));
    }

    [Fact]
    public void Deleting_a_job_removes_its_history_when_asked()
    {
        var (job, target) = Backup(withArchive: false);
        var other = _fx.NewJob("Other");
        var config = new AppConfig().WithJob(job).WithJob(other);

        var (updated, _) = _fx.Cleanup.DeleteJob(config, job.Id, cleanTargets: false, removeArchives: false, deleteHistory: true);

        Assert.Equal([other.Id], updated.Jobs.Select(j => j.Id));
        Assert.Empty(_fx.History.List());
        Assert.Equal(TargetState.Connected, _fx.Locator.Locate(target).State);
    }

    [Fact]
    public void Deleting_a_job_keeps_its_history_by_default()
    {
        var (job, _) = Backup(withArchive: false);
        var config = new AppConfig().WithJob(job);

        var (updated, report) = _fx.Cleanup.DeleteJob(config, job.Id, cleanTargets: true, removeArchives: true, deleteHistory: false);

        Assert.Empty(updated.Jobs);
        Assert.Equal(job.Id, Assert.Single(_fx.History.List()).JobId);
        Assert.DoesNotContain(report.Removed, r => r.Contains("history", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Clean_up_everything_keeps_the_executable_and_reports_disconnected_targets()
    {
        var (job, target) = Backup(withArchive: false);
        var (offlineJob, offline) = _fx.JobWithTarget("Offline");
        Directory.Move(offline.LastKnownPath, Path.Combine(_fx.BasePath, "unplugged"));
        File.WriteAllText(Path.Combine(_fx.AppDirectory, "Amberkeep.exe"), "exe");
        _fx.Create("app/state");
        _fx.Create("app/history");
        File.WriteAllText(Path.Combine(_fx.AppDirectory, "history", "my-notes.txt"), "not ours");
        var config = new AppConfig().WithJob(job).WithJob(offlineJob);
        _fx.Store.Save(config);

        var report = _fx.Cleanup.CleanUpEverything(config, cleanTargets: true, removeArchives: true);

        Assert.Empty(report.Errors);
        // Only what Amberkeep wrote is removed: state/ is not used before two-way sync, a foreign file keeps history/.
        Assert.Equal(
            ["Amberkeep.exe", "history", "state"],
            Directory.EnumerateFileSystemEntries(_fx.AppDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["my-notes.txt"], Directory.GetFiles(Path.Combine(_fx.AppDirectory, "history")).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(target.LastKnownPath, ".amberkeep")));
        Assert.True(File.Exists(Path.Combine(target.LastKnownPath, "data.txt")));
        Assert.Contains(report.NotCleaned, n => n.Contains("Offline", StringComparison.Ordinal));
    }

    [Fact]
    public void Foreign_marker_is_never_deleted()
    {
        var (job, target) = _fx.JobWithTarget();
        var other = _fx.NewJob("Other");
        _fx.Registry.AddTarget(new AppConfig(), other, target.LastKnownPath, "Taken over", replaceForeignMarker: true);

        var (_, report) = _fx.Cleanup.RemoveTarget(new AppConfig().WithJob(job), job.Id, target.Id, removeMarker: true, removeArchive: true);

        Assert.True(File.Exists(Path.Combine(target.LastKnownPath, ".amberkeep", "target.json")));
        Assert.Single(report.NotCleaned);
    }

    /// <summary>A job with one target that was backed up once (and has an archive snapshot when asked).</summary>
    private (JobConfig Job, TargetConfig Target) Backup(bool withArchive)
    {
        var (job, target) = _fx.JobWithTarget();
        job = job with { Archive = new ArchiveSettings { Enabled = withArchive } };
        _fx.Source.File("data.txt", "data");
        File.WriteAllText(Path.Combine(target.LastKnownPath, "old.txt"), "old");

        var prepared = _fx.Runner.AnalyzeAsync(job, target, cancellationToken: TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        var selection = prepared.Diff!.Items.Where(i => i.DefaultSelected).ToHashSet();
        _fx.Runner.ExecuteAsync(prepared, selection, approved: true, cancellationToken: TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        Assert.Equal(withArchive, Directory.Exists(Path.Combine(target.LastKnownPath, ".amberkeep", "archive")));
        return (job, target);
    }
}
