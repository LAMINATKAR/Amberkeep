using System.Text.Json.Nodes;
using Amberkeep.Core.Diff;
using Amberkeep.Core.Safety;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.Running;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.Storage;

/// <summary>Fixes from the code review of 1.0 in settings, validation and runs (Amberkeep's own folder, hand-edited values).</summary>
public sealed class StorageSafetyFixTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Target_inside_the_program_folder_cannot_be_saved()
    {
        var (job, _) = _fx.JobWithTarget();
        var around = job with
        {
            Targets = [job.Targets[0] with { LastKnownPath = Path.Combine(_fx.AppDirectory, "backup") }],
        };

        var errors = new AppConfig().Validate(around, _fx.AppDirectory);

        Assert.Contains(errors, e => e.Contains("folder of Amberkeep itself", StringComparison.Ordinal));
        Assert.DoesNotContain(new AppConfig().Validate(job, _fx.AppDirectory), e => e.Contains("Amberkeep itself", StringComparison.Ordinal));
    }

    [Fact]
    public void Source_inside_the_program_folder_cannot_be_saved_but_a_source_around_it_can()
    {
        var (job, _) = _fx.JobWithTarget();

        var inside = job with { SourcePath = _fx.Create("app/sub") };
        var around = job with { SourcePath = _fx.BasePath };

        Assert.Contains(new AppConfig().Validate(inside, _fx.AppDirectory), e => e.Contains("Amberkeep itself", StringComparison.Ordinal));
        Assert.DoesNotContain(new AppConfig().Validate(around, _fx.AppDirectory), e => e.Contains("Amberkeep itself", StringComparison.Ordinal));
    }

    [Fact]
    public void Adding_a_target_through_a_junction_around_the_source_is_refused()
    {
        var job = _fx.NewJob();
        var alias = Path.Combine(_fx.BasePath, "alias");
        Links.CreateDirectoryLinkOrSkip(alias, _fx.BasePath);

        var error = Assert.Throws<InvalidOperationException>(() => _fx.Registry.AddTarget(new AppConfig(), job, alias, "Alias"));

        Assert.Contains("overlaps the source", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_fx.BasePath, ".amberkeep")));
        Links.RemoveDirectoryLink(alias);
    }

    [Fact]
    public async Task Runner_skips_the_program_folder_inside_the_source()
    {
        var program = _fx.Create("source/Tools/Amberkeep");
        File.WriteAllText(Path.Combine(program, "amberkeep.json"), "{}");
        _fx.Source.File("photo.jpg");
        var (job, target) = _fx.JobWithTarget();
        var runner = new JobRunner(_fx.FileSystem, _fx.Volumes, _fx.History, _fx.Time) { ProgramDirectory = program };

        var run = await runner.AnalyzeAsync(job, target, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(run.Diff);
        Assert.Contains(run.Diff.Items, i => i.RelativePath == "photo.jpg");
        Assert.DoesNotContain(run.Diff.Items, i => i.RelativePath.StartsWith("Tools/Amberkeep", StringComparison.Ordinal));
        Assert.Contains(run.Diff.SafetyIssues, i => i.Kind == SafetyIssueKind.ProgramFolder);
    }

    [Fact]
    public async Task Job_without_a_source_path_gives_a_clear_error()
    {
        var (job, target) = _fx.JobWithTarget();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fx.Runner.AnalyzeAsync(job with { SourcePath = string.Empty }, target, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("source folder", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_hand_edited_values_are_corrected_on_load_never_towards_deleting_more()
    {
        var (job, target) = _fx.JobWithTarget();
        var copy = job with { Id = Guid.NewGuid(), Name = "Copy", Targets = [target] };
        _fx.Store.Save(new AppConfig().WithJob(job).WithJob(copy));

        var root = JsonNode.Parse(File.ReadAllText(_fx.Store.ConfigPath))!;
        var first = root["jobs"]![0]!;
        first["archive"]!["enabled"] = true;
        first["archive"]!["retentionDays"] = 0;
        first["massRemovalGuard"]!["percent"] = 150;
        first["massRemovalGuard"]!["count"] = -5;
        first["compareMode"] = 7;
        root["jobs"]![1]!["id"] = first["id"]!.GetValue<string>();
        File.WriteAllText(_fx.Store.ConfigPath, root.ToJsonString());

        var result = _fx.Store.Load();

        var jobs = result.Config!.Jobs;
        Assert.Null(jobs[0].Archive.RetentionDays);
        Assert.Equal(new MassRemovalGuard(true, 100, 0), jobs[0].MassRemovalGuard);
        Assert.Equal(CompareMode.SizeAndTime, jobs[0].CompareMode);
        Assert.NotEqual(jobs[0].Id, jobs[1].Id);
        Assert.NotEqual(jobs[0].Targets[0].Id, jobs[1].Targets[0].Id);
        Assert.NotNull(result.Warning);
        Assert.Contains("keep forever", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Retention_above_the_maximum_is_not_saved()
    {
        var (job, _) = _fx.JobWithTarget();

        var errors = new AppConfig().Validate(job with { Archive = new ArchiveSettings { Enabled = true, RetentionDays = 1_000_000 } });

        Assert.Contains(errors, e => e.Contains("retention", StringComparison.Ordinal));
    }

    [Fact]
    public void Valid_file_loads_without_a_warning()
    {
        var (job, _) = _fx.JobWithTarget();
        _fx.Store.Save(new AppConfig().WithJob(job));

        Assert.Null(_fx.Store.Load().Warning);
    }

    [Fact]
    public async Task History_pruning_removes_orphaned_change_lists_and_temporary_files_but_nothing_foreign()
    {
        var history = Directory.CreateDirectory(_fx.History.Directory).FullName;
        File.WriteAllText(Path.Combine(history, "20250101-000000-0123abcd.changes.json"), "[]");
        File.WriteAllText(Path.Combine(history, "20250101-000000-0123abcd.json.0123456789abcdef0123456789abcdef.tmp"), "x");
        File.WriteAllText(Path.Combine(history, "my-notes.json"), "{}");
        var (job, target) = _fx.JobWithTarget();
        _fx.Source.File("a.txt");
        var run = await _fx.Runner.AnalyzeAsync(job, target, cancellationToken: TestContext.Current.CancellationToken);

        await _fx.Runner.ExecuteAsync(run, run.Diff!.Items.ToHashSet(), approved: true, cancellationToken: TestContext.Current.CancellationToken);

        var names = Directory.GetFiles(history).Select(Path.GetFileName).ToList();
        Assert.Contains("my-notes.json", names);
        Assert.DoesNotContain("20250101-000000-0123abcd.changes.json", names);
        Assert.DoesNotContain(names, n => n!.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Single(_fx.History.List());
    }

    [Fact]
    public void Target_at_the_root_of_a_disk_is_found_after_the_drive_letter_changed()
    {
        // VolumeA plays the disk root; the target was the whole disk at a letter that no longer exists.
        var job = _fx.NewJob();
        var target = _fx.Registry.AddTarget(new AppConfig(), job, _fx.VolumeA, "Whole disk");
        var moved = target with { LastKnownPath = Path.Combine(_fx.BasePath, "gone-letter") };

        var status = _fx.Locator.Locate(moved);

        Assert.True(status.IsConnected);
        Assert.Equal(Path.TrimEndingDirectorySeparator(_fx.VolumeA), Path.TrimEndingDirectorySeparator(status.Path!));
    }

    [Fact]
    public void Newer_file_that_this_version_cannot_read_is_not_treated_as_damaged()
    {
        const string Newer = """{ "version": 2, "jobs": [ { "name": "X", "compareMode": "SomethingNew" } ] }""";
        File.WriteAllText(_fx.Store.ConfigPath, Newer);

        var result = _fx.Store.Load();

        Assert.Null(result.Config);
        Assert.Contains("newer version", result.Error, StringComparison.Ordinal);
        Assert.Equal(Newer, File.ReadAllText(_fx.Store.ConfigPath));
        Assert.Single(Directory.GetFiles(_fx.AppDirectory));
        Assert.Throws<InvalidOperationException>(() => _fx.Store.Save(new AppConfig()));
    }

    [Fact]
    public void Last_run_in_the_future_does_not_stop_the_schedule()
    {
        var daily = new ScheduleConfig { Kind = ScheduleKind.Daily, Time = new TimeOnly(20, 0) };
        var now = new DateTimeOffset(2026, 11, 14, 21, 0, 0, TimeSpan.Zero);

        // The clock was years ahead once: a record says the last run was in 2099.
        var lastRun = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Such a record is ignored: counted from the start of the target, a missed run is due once ...
        Assert.True(ScheduleCalculator.IsDue(daily, lastRun, now.AddDays(-30), now, TimeZoneInfo.Utc));

        // ... and a target created after today's time waits for tomorrow, as usual.
        Assert.False(ScheduleCalculator.IsDue(daily, lastRun, now.AddMinutes(-30), now, TimeZoneInfo.Utc));
        Assert.True(ScheduleCalculator.IsDue(daily, lastRun, now.AddMinutes(-30), now.AddDays(1), TimeZoneInfo.Utc));
    }
}
