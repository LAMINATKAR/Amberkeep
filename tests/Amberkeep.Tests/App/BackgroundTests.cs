using Amberkeep.App.Platform;
using Amberkeep.App.Services;
using Amberkeep.App.ViewModels;
using Amberkeep.Storage.Cleanup;
using Amberkeep.Storage.Config;

namespace Amberkeep.Tests.App;

internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Message, Action? Click)> Shown { get; } = [];

    public void Show(string title, string message, Action? onClick = null) => Shown.Add((title, message, onClick));
}

internal sealed class FakeAutostart : IAutostart
{
    public bool IsEnabled { get; private set; }

    public int EnableCalls { get; private set; }

    public void Enable()
    {
        IsEnabled = true;
        EnableCalls++;
    }

    public void Remove() => IsEnabled = false;
}

/// <summary>Background runs, automation triggers, autostart and single instance.</summary>
public sealed class BackgroundTests : IDisposable
{
    private readonly MutableTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeAutostart _autostart = new();
    private readonly AppFixture _app;
    private readonly FakeNotifier _notifier = new();

    public BackgroundTests() => _app = new AppFixture(_time, _autostart);

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Job_requiring_approval_only_prepares_a_review()
    {
        var (job, target) = _app.SavedJob(requireApproval: true);
        _app.Source.File("a.txt");

        var result = await Runner().RunAsync(job, target, Ct);

        Assert.Equal(BackgroundRunResult.WaitingForReview, result);
        Assert.True(_app.Services.PendingReviews.Contains(target.Id));
        Assert.Contains(_notifier.Shown, n => n.Title.Contains("1 change", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(target.LastKnownPath, "a.txt")));
        Assert.False(_app.Services.Gate.IsBusy);
    }

    [Fact]
    public async Task Job_without_approval_runs_and_notifies_the_result()
    {
        var (job, target) = _app.SavedJob(requireApproval: false);
        _app.Source.File("a.txt", "a");

        var result = await Runner().RunAsync(job, target, Ct);

        Assert.Equal(BackgroundRunResult.Completed, result);
        Assert.Equal("a", File.ReadAllText(Path.Combine(target.LastKnownPath, "a.txt")));
        Assert.Contains(_notifier.Shown, n => n.Title.EndsWith("completed", StringComparison.Ordinal));
        Assert.Single(_app.Services.History.List());
    }

    [Fact]
    public async Task Up_to_date_target_does_nothing()
    {
        var (job, target) = _app.SavedJob(requireApproval: false);

        Assert.Equal(BackgroundRunResult.UpToDate, await Runner().RunAsync(job, target, Ct));
        Assert.Empty(_notifier.Shown);
        Assert.Empty(_app.Services.History.List());
    }

    [Fact]
    public async Task Mass_removal_waits_for_review_even_without_approval_setting()
    {
        var (job, target) = _app.SavedJob(requireApproval: false);
        for (var i = 0; i < 120; i++)
        {
            File.WriteAllText(Path.Combine(target.LastKnownPath, $"{i}.txt"), "x");
        }

        _app.Source.File("0.txt", "x");

        Assert.Equal(BackgroundRunResult.WaitingForReview, await Runner().RunAsync(job, target, Ct));
        Assert.True(File.Exists(Path.Combine(target.LastKnownPath, "50.txt")));
    }

    [Fact]
    public async Task Busy_gate_defers_the_run_and_disabled_notifications_stay_silent()
    {
        var (job, target) = _app.SavedJob(requireApproval: true);
        _app.Source.File("a.txt");
        _app.Services.Save(_app.Services.Config with { Settings = _app.Services.Config.Settings with { Notifications = false } });

        Assert.True(_app.Services.Gate.TryEnter());
        Assert.Equal(BackgroundRunResult.Busy, await Runner().RunAsync(job, target, Ct));
        _app.Services.Gate.Exit();

        Assert.Equal(BackgroundRunResult.WaitingForReview, await Runner().RunAsync(job, target, Ct));
        Assert.Empty(_notifier.Shown);
    }

    [Fact]
    public async Task Connecting_a_target_starts_a_run_but_one_connected_at_start_does_not()
    {
        var (job, target) = SavedAutomaticJob(onConnect: true);
        var runs = new List<Guid>();
        using var engine = Engine(runs);

        await engine.TickAsync();
        await engine.TickAsync();
        Assert.Empty(runs);

        var unplugged = Path.Combine(_app.BasePath, "unplugged");
        Directory.Move(target.LastKnownPath, unplugged);
        await engine.TickAsync();
        Directory.Move(unplugged, target.LastKnownPath);
        await engine.TickAsync();

        Assert.Equal([target.Id], runs);
        Assert.NotEqual(Guid.Empty, job.Id);
    }

    [Fact]
    public async Task Schedule_runs_when_due_and_not_again_right_after()
    {
        var created = _time.Now;
        SavedAutomaticJob(schedule: new ScheduleConfig { Kind = ScheduleKind.Interval, IntervalHours = 2 });
        var runs = new List<Guid>();
        using var engine = Engine(runs);

        await engine.TickAsync();
        _time.Now = created.AddHours(1);
        await engine.TickAsync();
        Assert.Empty(runs);

        _time.Now = created.AddHours(2).AddMinutes(1);
        await engine.TickAsync();
        await engine.TickAsync();
        Assert.Single(runs);
    }

    [Fact]
    public async Task Paused_automation_and_pending_reviews_start_nothing()
    {
        var (job, target) = SavedAutomaticJob(schedule: new ScheduleConfig { Kind = ScheduleKind.Interval, IntervalHours = 1 });
        _time.Now = _time.Now.AddHours(3);
        var runs = new List<Guid>();
        using var engine = Engine(runs);
        await engine.TickAsync();
        runs.Clear();
        _time.Now = _time.Now.AddHours(3);

        _app.Services.Save(_app.Services.Config with { Settings = _app.Services.Config.Settings with { AutomaticRunsPaused = true } });
        await engine.TickAsync();
        Assert.Empty(runs);

        _app.Services.Save(_app.Services.Config with { Settings = _app.Services.Config.Settings with { AutomaticRunsPaused = false } });
        _app.Services.PendingReviews.Add(new PendingReview(job, target, 3, _time.Now));
        await engine.TickAsync();
        Assert.Empty(runs);
    }

    [Fact]
    public async Task Busy_run_is_retried_on_the_next_check()
    {
        var (_, target) = SavedAutomaticJob(onConnect: true);
        var runs = new List<Guid>();
        var answers = new Queue<BackgroundRunResult>([BackgroundRunResult.Busy, BackgroundRunResult.Completed]);
        using var engine = new AutomationEngine(_app.Services, (_, t, _) =>
        {
            runs.Add(t.Id);
            return Task.FromResult(answers.Dequeue());
        });

        var unplugged = Path.Combine(_app.BasePath, "unplugged");
        Directory.Move(target.LastKnownPath, unplugged);
        await engine.TickAsync();
        Directory.Move(unplugged, target.LastKnownPath);
        await engine.TickAsync();
        await engine.TickAsync();
        await engine.TickAsync();

        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public void Settings_switch_autostart_and_save_background_options()
    {
        var settings = new SettingsViewModel(_app.Shell) { StartWithSystem = true, CloseToTray = false, Notifications = false };

        Assert.True(_autostart.IsEnabled);
        var saved = _app.Services.Config.Settings;
        Assert.True(saved.StartWithSystem);
        Assert.False(saved.CloseToTray);
        Assert.False(saved.Notifications);

        settings.StartWithSystem = false;
        Assert.False(_autostart.IsEnabled);
    }

    [Fact]
    public void Startup_sync_refreshes_or_removes_the_autostart_entry()
    {
        _app.Services.Save(_app.Services.Config with { Settings = _app.Services.Config.Settings with { StartWithSystem = true } });
        Assert.Null(_app.Services.SyncAutostart());
        Assert.Equal(1, _autostart.EnableCalls);

        _app.Services.Save(_app.Services.Config with { Settings = _app.Services.Config.Settings with { StartWithSystem = false } });
        Assert.Null(_app.Services.SyncAutostart());
        Assert.False(_autostart.IsEnabled);
    }

    [Fact]
    public void Editor_saves_triggers()
    {
        var (job, _) = _app.SavedJob();
        var editor = new JobEditorViewModel(_app.Shell, job)
        {
            RunOnConnect = true,
        };
        editor.ScheduleKind = editor.ScheduleChoices.Single(c => c.Value == ScheduleKind.Weekly);
        editor.ScheduleDay = editor.DayChoices.Single(d => d.Value == DayOfWeek.Friday);
        editor.ScheduleTime = new TimeSpan(18, 30, 0);

        var trigger = editor.BuildJob().Trigger;

        Assert.True(trigger.OnTargetConnected);
        Assert.Equal(new ScheduleConfig { Kind = ScheduleKind.Weekly, DayOfWeek = DayOfWeek.Friday, Time = new TimeOnly(18, 30) }, trigger.Schedule);
        Assert.True(editor.ShowDay && editor.ShowTime && !editor.ShowInterval);
    }

    [Fact]
    public void Dashboard_shows_waiting_reviews_and_paused_automation()
    {
        var (job, target) = SavedAutomaticJob(onConnect: true);
        var dashboard = _app.Shell.Dashboard;

        _app.Services.PendingReviews.Add(new PendingReview(job, target, 5, _time.Now));
        Assert.Contains("5 changes", dashboard.PendingText, StringComparison.Ordinal);
        Assert.True(_app.Services.Activity.State.NeedsAttention);

        _app.Services.Save(_app.Services.Config with { Settings = _app.Services.Config.Settings with { AutomaticRunsPaused = true } });
        Assert.True(dashboard.IsPaused);
        dashboard.ResumeCommand.Execute(null);
        Assert.False(_app.Services.Config.Settings.AutomaticRunsPaused);
    }

    [Fact]
    public void Desktop_and_launch_agent_entries_are_written_and_removed()
    {
        var folder = _app.Create("autostart");
        IAutostart[] entries =
        [
            new DesktopFileAutostart(folder, "Amberkeep-test", "/opt/amberkeep/Amberkeep"),
            new LaunchAgentAutostart(folder, "Amberkeep-test", "/Applications/Amber & keep/Amberkeep"),
        ];

        foreach (var entry in entries)
        {
            entry.Enable();
            Assert.True(entry.IsEnabled);
            entry.Remove();
            Assert.False(entry.IsEnabled);
        }

        entries[1].Enable();
        Assert.Contains("Amber &amp; keep", File.ReadAllText(((LaunchAgentAutostart)entries[1]).FilePath), StringComparison.Ordinal);
        Assert.Contains(Autostart.BackgroundArgument, File.ReadAllText(((LaunchAgentAutostart)entries[1]).FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_entry_is_written_and_removed_under_a_test_key()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry exists only on Windows.");
        if (OperatingSystem.IsWindows())
        {
            RegistryRoundTrip();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegistryRoundTrip()
    {
        var key = $@"Software\Amberkeep-Tests\{Guid.NewGuid():N}";
        try
        {
            var entry = new RegistryAutostart(key, "Amberkeep-test", @"C:\Tools\Amberkeep\Amberkeep.exe");
            entry.Enable();
            Assert.True(entry.IsEnabled);
            using (var opened = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key))
            {
                Assert.Equal(@"""C:\Tools\Amberkeep\Amberkeep.exe"" --background", opened!.GetValue("Amberkeep-test"));
            }

            entry.Remove();
            Assert.False(entry.IsEnabled);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Amberkeep-Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Enabling_removes_entries_of_copies_that_no_longer_exist()
    {
        var folder = _app.Create("autostart-stale");
        var existingExe = Path.Combine(_app.Create("other-copy"), "Amberkeep.exe");
        File.WriteAllText(existingExe, "exe");
        new DesktopFileAutostart(folder, "Amberkeep-moved", Path.Combine(_app.BasePath, "gone", "Amberkeep")).Enable();
        new DesktopFileAutostart(folder, "Amberkeep-other", existingExe).Enable();

        new DesktopFileAutostart(folder, "Amberkeep-current", existingExe).Enable();

        Assert.Equal(
            ["Amberkeep-current.desktop", "Amberkeep-other.desktop"],
            Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Newly_enabled_connect_trigger_waits_for_the_next_connection()
    {
        var (job, target) = _app.SavedJob();
        var runs = new List<Guid>();
        using var engine = Engine(runs);
        await engine.TickAsync();

        _app.Services.Save(_app.Services.Config.WithJob(job with { Trigger = new TriggerConfig { OnTargetConnected = true } }));
        await engine.TickAsync();
        await engine.TickAsync();

        Assert.Empty(runs);
        Assert.NotEqual(Guid.Empty, target.Id);
    }

    [Fact]
    public void Problems_keep_the_tray_badge_until_cleared_while_reviews_come_and_go()
    {
        var activity = _app.Services.Activity;

        activity.ReportProblem();
        activity.SetReviewsWaiting(false);
        Assert.True(activity.State.NeedsAttention);

        activity.ClearProblem();
        Assert.False(activity.State.NeedsAttention);
    }

    [Fact]
    public void Missing_creation_time_counts_as_missed_schedule()
    {
        var daily = new ScheduleConfig { Kind = ScheduleKind.Daily };

        Assert.True(ScheduleCalculator.IsDue(daily, null, DateTimeOffset.MinValue, DateTimeOffset.UtcNow, TimeZoneInfo.Local));
    }

    [Fact]
    public void Autostart_entry_names_differ_per_program_folder()
    {
        var a = Autostart.EntryName(Path.Combine(_app.BasePath, "a", "Amberkeep.exe"));
        var b = Autostart.EntryName(Path.Combine(_app.BasePath, "b", "Amberkeep.exe"));

        Assert.StartsWith("Amberkeep-", a, StringComparison.Ordinal);
        Assert.NotEqual(a, b);
        Assert.Equal(a, Autostart.EntryName(Path.Combine(_app.BasePath, "a", "Amberkeep.exe")));
    }

    [Fact]
    public async Task Second_instance_in_the_same_folder_asks_the_first_to_show_itself()
    {
        var folder = _app.Create("instance-a");
        using var first = SingleInstance.Acquire(folder);
        var shown = new TaskCompletionSource();
        first.ShowRequested += (_, _) => shown.TrySetResult();

        using var second = SingleInstance.Acquire(folder);
        using var other = SingleInstance.Acquire(_app.Create("instance-b"));

        Assert.True(first.IsFirst);
        Assert.False(second.IsFirst);
        Assert.True(other.IsFirst);
        Assert.True(second.SignalFirst(TimeSpan.FromSeconds(5)));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    private BackgroundRunner Runner() => new(_app.Services, _notifier);

    private AutomationEngine Engine(List<Guid> runs) => new(_app.Services, (_, target, _) =>
    {
        runs.Add(target.Id);
        return Task.FromResult(BackgroundRunResult.Completed);
    });

    private (JobConfig Job, TargetConfig Target) SavedAutomaticJob(bool onConnect = false, ScheduleConfig? schedule = null)
    {
        var (job, target) = _app.SavedJob();
        job = job with { Trigger = new TriggerConfig { OnTargetConnected = onConnect, Schedule = schedule ?? new ScheduleConfig() } };
        _app.Services.Save(_app.Services.Config.WithJob(job));
        return (job, target);
    }
}
