using Amberkeep.Core.Execution;
using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.History;

namespace Amberkeep.Tests.Storage;

/// <summary>Nested paths across jobs, newer-version protection, null tolerance, clean-up leftovers, split history.</summary>
public sealed class ReviewFixTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Target_inside_another_jobs_target_is_rejected()
    {
        var (_, outer) = _fx.JobWithTarget("All");
        var photos = _fx.NewJob("Photos");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _fx.Registry.AddTarget(_fx.Config, photos, Path.Combine(outer.LastKnownPath, "Photos"), "Nested"));

        Assert.Contains("All", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Target_around_another_jobs_target_is_rejected()
    {
        _fx.JobWithTarget("Photos");
        var all = _fx.NewJob("All");

        Assert.Throws<InvalidOperationException>(() =>
            _fx.Registry.AddTarget(_fx.Config, all, Path.Combine(_fx.VolumeA, "Backup"), "Around"));
    }

    [Fact]
    public void Target_inside_another_jobs_source_is_rejected()
    {
        _fx.JobWithTarget("Documents");
        var other = _fx.NewJob("Other") with { SourcePath = _fx.Create("other-source") };

        Assert.Throws<InvalidOperationException>(() =>
            _fx.Registry.AddTarget(_fx.Config, other, _fx.Source.FullPath("backup-of-other"), "Into a source"));
    }

    [Fact]
    public void Config_validation_finds_nested_targets_of_the_same_job()
    {
        var (job, target) = _fx.JobWithTarget();
        var nested = job.WithTarget(new TargetConfig { Name = "Nested", LastKnownPath = Path.Combine(target.LastKnownPath, "inner") });

        Assert.Contains(_fx.Config.Validate(nested), e => e.Contains("Nested", StringComparison.Ordinal));
        Assert.Empty(_fx.Config.Validate(job));
    }

    [Fact]
    public void Settings_from_a_newer_version_are_never_overwritten()
    {
        File.WriteAllText(_fx.Store.ConfigPath, """{ "version": 99 }""");
        _fx.Store.Load();

        Assert.Throws<InvalidOperationException>(() => _fx.Store.Save(new AppConfig()));
        Assert.Contains("99", File.ReadAllText(_fx.Store.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Nulls_in_a_hand_edited_file_become_defaults()
    {
        File.WriteAllText(_fx.Store.ConfigPath, """
            { "version": 1, "settings": null,
              "jobs": [ { "name": "Photos", "sourcePath": "C:\\x", "exclusions": null, "targets": null, "archive": null } ] }
            """);

        var config = _fx.Store.Load().Config!;

        var job = Assert.Single(config.Jobs);
        Assert.NotNull(config.Settings);
        Assert.Empty(job.Exclusions);
        Assert.Empty(job.Targets);
        Assert.NotNull(job.Archive);
        Assert.DoesNotContain(job.Validate(), e => e.Contains("Archive", StringComparison.Ordinal));
    }

    [Fact]
    public void Clean_up_everything_also_removes_damaged_copies_and_temporary_files()
    {
        foreach (var name in new[] { "amberkeep.json", "amberkeep.json.bad-20260101-000000", "amberkeep.json.0123456789abcdef0123456789abcdef.tmp", ".amberkeep-write-test-abc", "amberkeep-error.log", "Amberkeep.exe", "readme.txt", "amberkeep.json.my-copy" })
        {
            File.WriteAllText(Path.Combine(_fx.AppDirectory, name), "x");
        }

        var report = _fx.Cleanup.CleanUpEverything(new AppConfig(), cleanTargets: false, removeArchives: false);

        Assert.Empty(report.Errors);
        Assert.Equal(
            ["Amberkeep.exe", "amberkeep.json.my-copy", "readme.txt"],
            Directory.GetFiles(_fx.AppDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Changes_are_stored_beside_the_record_and_pruned_with_it()
    {
        var first = Record(DateTimeOffset.UtcNow.AddHours(-1), "old.txt");
        var second = Record(DateTimeOffset.UtcNow, "new.txt");

        _fx.History.Add(first, limit: 1);
        _fx.History.Add(second, limit: 1);

        var listed = Assert.Single(_fx.History.List());
        Assert.Empty(listed.SelectedChanges);
        Assert.Equal("new.txt", Assert.Single(_fx.History.ReadChanges(listed)).Path);
        Assert.Equal(2, Directory.GetFiles(_fx.History.Directory).Length);
        Assert.DoesNotContain("new.txt", File.ReadAllText(Directory.GetFiles(_fx.History.Directory, "*-????????.json").Single()), StringComparison.Ordinal);
    }

    private static RunRecord Record(DateTimeOffset started, string change) => new()
    {
        RunId = Guid.NewGuid(),
        JobId = Guid.NewGuid(),
        TargetId = Guid.NewGuid(),
        StartedUtc = started,
        Outcome = ExecutionOutcome.Completed,
        SelectedChanges = [new RecordedChange(change, EntryKind.File, Core.Diff.DiffCategory.New)],
    };
}
