using Amberkeep.Core.Diff;
using Amberkeep.Core.Paths;
using Amberkeep.Core.Safety;
using Amberkeep.Storage;
using Amberkeep.Storage.Config;

namespace Amberkeep.Tests.Storage;

public sealed class AppStoreTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var result = _fx.Store.Load();

        Assert.NotNull(result.Config);
        Assert.Null(result.Warning);
        Assert.Null(result.Error);
        Assert.Empty(result.Config.Jobs);
        Assert.Equal(100, result.Config.Settings.HistoryLimit);
    }

    [Fact]
    public void Saved_configuration_loads_back_unchanged()
    {
        var (job, _) = _fx.JobWithTarget();
        job = job with
        {
            CompareMode = CompareMode.Content,
            CaseSensitivity = CaseSensitivity.Sensitive,
            MassRemovalGuard = new MassRemovalGuard(true, 25, 10),
            Archive = new ArchiveSettings { Enabled = true, RetentionDays = null },
            Exclusions = ["*.tmp", "node_modules/"],
        };
        var config = new AppConfig { Settings = new AppSettings { HistoryLimit = 7, CloseToTray = false } }.WithJob(job);

        _fx.Store.Save(config);
        var loaded = _fx.Store.Load().Config!;

        Assert.Equal(config.Settings, loaded.Settings);
        var loadedJob = Assert.Single(loaded.Jobs);
        Assert.Equal(job with { Exclusions = [], Targets = [] }, loadedJob with { Exclusions = [], Targets = [] });
        Assert.Equal(job.Exclusions, loadedJob.Exclusions);
        Assert.Equal(job.Targets, loadedJob.Targets);
        Assert.Contains("\"compareMode\": \"Content\"", File.ReadAllText(_fx.Store.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Schedules_load_back_and_the_old_single_weekly_day_is_taken_over()
    {
        var (job, _) = _fx.JobWithTarget();
        var weekly = new ScheduleConfig { Kind = ScheduleKind.Weekly, DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Thursday], Time = new TimeOnly(6, 45) };
        _fx.Store.Save(new AppConfig().WithJob(job with { Trigger = new TriggerConfig { Schedule = weekly } }));

        var text = File.ReadAllText(_fx.Store.ConfigPath);
        Assert.Equal(weekly, Assert.Single(_fx.Store.Load().Config!.Jobs).Trigger.Schedule);
        Assert.DoesNotContain("\"dayOfWeek\"", text, StringComparison.Ordinal);

        // A file written before weekly schedules had several days.
        var root = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        var stored = root["jobs"]![0]!["trigger"]!["schedule"]!.AsObject();
        Assert.True(stored.Remove("daysOfWeek"));
        stored["dayOfWeek"] = "Friday";
        File.WriteAllText(_fx.Store.ConfigPath, root.ToJsonString());

        var schedule = Assert.Single(_fx.Store.Load().Config!.Jobs).Trigger.Schedule;
        Assert.Equal([DayOfWeek.Friday], schedule.DaysOfWeek);
        Assert.Null(schedule.LegacyDayOfWeek);
    }

    [Fact]
    public void Weekly_schedule_without_days_and_monthly_day_out_of_range_are_invalid()
    {
        var (job, _) = _fx.JobWithTarget();
        var config = new AppConfig();

        var noDays = job with { Trigger = new TriggerConfig { Schedule = new ScheduleConfig { Kind = ScheduleKind.Weekly, DaysOfWeek = [] } } };
        var day32 = job with { Trigger = new TriggerConfig { Schedule = new ScheduleConfig { Kind = ScheduleKind.Monthly, DayOfMonth = 32 } } };
        var day31 = job with { Trigger = new TriggerConfig { Schedule = new ScheduleConfig { Kind = ScheduleKind.Monthly, DayOfMonth = 31 } } };

        Assert.Contains(config.Validate(noDays), e => e.Contains("at least one day", StringComparison.Ordinal));
        Assert.Contains(config.Validate(day32), e => e.Contains("day of the month", StringComparison.Ordinal));
        Assert.DoesNotContain(config.Validate(day31), e => e.Contains("day of the month", StringComparison.Ordinal));
    }

    [Fact]
    public void Saving_leaves_no_temporary_files()
    {
        _fx.Store.Save(new AppConfig());
        _fx.Store.Save(new AppConfig());

        Assert.Equal([AppStore.ConfigFileName], Directory.GetFiles(_fx.AppDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public void Damaged_file_is_set_aside_and_defaults_are_used()
    {
        File.WriteAllText(_fx.Store.ConfigPath, "{ not json");

        var result = _fx.Store.Load();

        Assert.NotNull(result.Config);
        Assert.NotNull(result.Warning);
        Assert.False(File.Exists(_fx.Store.ConfigPath));
        var damaged = Assert.Single(Directory.GetFiles(_fx.AppDirectory, "amberkeep.json.bad-*"));
        Assert.Equal("{ not json", File.ReadAllText(damaged));
    }

    [Fact]
    public void Newer_format_is_refused_and_left_untouched()
    {
        File.WriteAllText(_fx.Store.ConfigPath, """{ "version": 99, "jobs": [] }""");

        var result = _fx.Store.Load();

        Assert.Null(result.Config);
        Assert.NotNull(result.Error);
        Assert.Contains("\"version\": 99", File.ReadAllText(_fx.Store.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Unwritable_program_folder_is_reported()
    {
        var file = Path.Combine(_fx.BasePath, "not-a-folder");
        File.WriteAllText(file, "x");
        var store = new AppStore(Path.Combine(file, "app"));

        Assert.Null(_fx.Store.CheckWritable());
        Assert.Contains("cannot write", store.CheckWritable(), StringComparison.Ordinal);
    }
}
