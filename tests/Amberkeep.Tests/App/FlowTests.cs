using Amberkeep.App.ViewModels;
using Amberkeep.Core.Execution;
using Amberkeep.Storage;
using Amberkeep.Storage.Config;

namespace Amberkeep.Tests.App;

/// <summary>Dashboard, editor, run and one-time run view models end to end on temp folders.</summary>
public sealed class FlowTests : IDisposable
{
    private readonly AppFixture _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Dashboard_shows_target_states_and_last_run()
    {
        _app.SavedJob();
        var dashboard = _app.Shell.Dashboard;

        await dashboard.RefreshAsync();

        var row = Assert.Single(Assert.Single(dashboard.Jobs).Targets);
        Assert.Equal(TargetDisplayState.Connected, row.State);
        Assert.Equal("Never backed up", row.LastRunText);
    }

    [Fact]
    public async Task Target_without_marker_is_shown_as_not_verified()
    {
        var (_, target) = _app.SavedJob();
        File.Delete(Path.Combine(target.LastKnownPath, ".amberkeep", "target.json"));

        await _app.Shell.Dashboard.RefreshAsync();

        var row = _app.Shell.Dashboard.Jobs[0].Targets[0];
        Assert.Equal(TargetDisplayState.Unverified, row.State);
        Assert.True(row.CanLocate);
        Assert.False(row.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Run_of_a_job_without_review_mirrors_straight_away()
    {
        var (_, target) = _app.SavedJob(requireApproval: false);
        _app.Source.File("a.txt", "a");
        await _app.Shell.Dashboard.RefreshAsync();

        await _app.Shell.Dashboard.Jobs[0].Targets[0].RunCommand.ExecuteAsync(null);
        var run = await _app.WaitForPageAsync<RunViewModel>(r => r.IsFinished);

        Assert.Equal(ExecutionOutcome.Completed, run.Outcome!.Outcome);
        Assert.Equal("a", File.ReadAllText(Path.Combine(target.LastKnownPath, "a.txt")));
        Assert.Single(_app.Services.History.List());
    }

    [Fact]
    public async Task Run_of_a_job_with_review_shows_the_diff_first()
    {
        var (_, target) = _app.SavedJob(requireApproval: true);
        _app.Source.File("a.txt", "a");
        await _app.Shell.Dashboard.RefreshAsync();

        await _app.Shell.Dashboard.Jobs[0].Targets[0].RunCommand.ExecuteAsync(null);
        var diff = await _app.WaitForPageAsync<DiffViewModel>();

        Assert.False(File.Exists(Path.Combine(target.LastKnownPath, "a.txt")));

        await diff.RunCommand.ExecuteAsync(null);
        var run = await _app.WaitForPageAsync<RunViewModel>(r => r.IsFinished);
        Assert.Equal(ExecutionOutcome.Completed, run.Outcome!.Outcome);
        Assert.True(File.Exists(Path.Combine(target.LastKnownPath, "a.txt")));
    }

    [Fact]
    public async Task Deleting_a_job_asks_and_removes_it()
    {
        var (job, target) = _app.SavedJob();
        await _app.Shell.Dashboard.RefreshAsync();
        _app.Dialogs.OptionAnswers.Enqueue([true, false, false]);

        await _app.Shell.Dashboard.Jobs[0].DeleteCommand.ExecuteAsync(null);

        Assert.Empty(_app.Services.Config.Jobs);
        Assert.Empty(new AppStore(_app.AppDirectory).Load().Config!.Jobs);
        Assert.False(File.Exists(Path.Combine(target.LastKnownPath, ".amberkeep", "target.json")));
        Assert.Empty(_app.Shell.Dashboard.Jobs);
        Assert.NotEqual(Guid.Empty, job.Id);
    }

    [Fact]
    public async Task Editor_validates_and_saves_a_new_job_with_a_target()
    {
        var editor = new JobEditorViewModel(_app.Shell, null);
        Assert.False(editor.CanSave);

        editor.Name = "Photos";
        editor.SourcePath = _app.Source.Root;
        Assert.True(editor.CanSave);

        var folder = Path.Combine(_app.VolumeA, "Backup");
        _app.Dialogs.Folders.Enqueue(folder);
        await editor.AddTargetCommand.ExecuteAsync(null);
        Assert.False(File.Exists(Path.Combine(folder, ".amberkeep", "target.json")));

        await editor.SaveCommand.ExecuteAsync(null);

        var job = Assert.Single(_app.Services.Config.Jobs);
        Assert.Equal("Photos", job.Name);
        Assert.Equal(folder, Assert.Single(job.Targets).LastKnownPath);
        Assert.True(File.Exists(Path.Combine(folder, ".amberkeep", "target.json")));
        Assert.Same(_app.Shell.Dashboard, _app.Shell.CurrentPage);
    }

    [Fact]
    public void New_editor_shows_no_errors_until_touched()
    {
        var editor = new JobEditorViewModel(_app.Shell, null);

        Assert.Empty(editor.Errors);
        Assert.False(editor.CanSave);

        editor.Name = "x";
        Assert.NotEmpty(editor.Errors);
    }

    [Fact]
    public async Task Failed_save_removes_markers_written_for_new_targets()
    {
        var editor = new JobEditorViewModel(_app.Shell, null) { Name = "Photos", SourcePath = _app.Source.Root };
        var good = Path.Combine(_app.VolumeA, "good");
        var bad = Path.Combine(_app.VolumeB, "bad");
        _app.Dialogs.Folders.Enqueue(good);
        await editor.AddTargetCommand.ExecuteAsync(null);
        _app.Dialogs.Folders.Enqueue(bad);
        await editor.AddTargetCommand.ExecuteAsync(null);
        File.WriteAllText(bad, "now a file");

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Empty(_app.Services.Config.Jobs);
        Assert.False(File.Exists(Path.Combine(good, ".amberkeep", "target.json")));
        Assert.Contains(_app.Dialogs.Messages, m => m.StartsWith("The backup could not be saved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_time_run_with_archive_keeps_old_snapshots_of_saved_backups()
    {
        _app.Source.File("a.txt", "new");
        var target = _app.Create("shared-target");
        var oldSnapshot = Path.Combine(target, ".amberkeep", "archive", "2020-01-01_120000");
        Directory.CreateDirectory(oldSnapshot);
        File.WriteAllText(Path.Combine(target, "a.txt"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(target, "a.txt"), DateTime.UtcNow.AddDays(-1));
        var oneTime = new OneTimeRunViewModel(_app.Shell) { SourcePath = _app.Source.Root, TargetPath = target, ArchiveEnabled = true };

        await oneTime.AnalyzeCommand.ExecuteAsync(null);
        await (await _app.WaitForPageAsync<DiffViewModel>()).RunCommand.ExecuteAsync(null);
        await _app.WaitForPageAsync<RunViewModel>(r => r.IsFinished);

        Assert.True(Directory.Exists(oldSnapshot));
    }

    [Fact]
    public async Task Editor_refuses_a_target_inside_the_source()
    {
        var editor = new JobEditorViewModel(_app.Shell, null) { Name = "Photos", SourcePath = _app.Source.Root };
        _app.Dialogs.Folders.Enqueue(_app.Source.FullPath("backup"));

        await editor.AddTargetCommand.ExecuteAsync(null);

        Assert.Empty(editor.Targets);
        Assert.Contains(_app.Dialogs.Messages, m => m.StartsWith("Cannot use this folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Editor_asks_before_taking_over_a_folder_of_another_backup()
    {
        var (_, target) = _app.SavedJob("Other");
        var editor = new JobEditorViewModel(_app.Shell, null) { Name = "Photos", SourcePath = _app.Create("photos") };
        _app.Services.Accept(new AppConfig());
        _app.Dialogs.Folders.Enqueue(target.LastKnownPath);
        _app.Dialogs.Confirmations.Enqueue(false);

        await editor.AddTargetCommand.ExecuteAsync(null);

        Assert.Empty(editor.Targets);
        Assert.Contains("Folder already used", _app.Dialogs.Messages);
    }

    [Fact]
    public async Task Editor_removes_a_target_with_its_marker_on_save()
    {
        var (job, target) = _app.SavedJob();
        var editor = new JobEditorViewModel(_app.Shell, job);
        _app.Dialogs.OptionAnswers.Enqueue([true, false]);

        await editor.RemoveTargetCommand.ExecuteAsync(editor.Targets[0]);
        Assert.True(File.Exists(Path.Combine(target.LastKnownPath, ".amberkeep", "target.json")));
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Empty(Assert.Single(_app.Services.Config.Jobs).Targets);
        Assert.False(File.Exists(Path.Combine(target.LastKnownPath, ".amberkeep", "target.json")));
    }

    [Fact]
    public async Task Cancelled_run_reports_cancelled()
    {
        var run = new RunViewModel(_app.Shell, "t", "s", "t", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        _app.Dialogs.Confirmations.Enqueue(true);

        var running = run.StartAsync();
        await run.CancelCommand.ExecuteAsync(null);
        await running;

        Assert.Equal(ExecutionOutcome.Cancelled, run.Outcome!.Outcome);
        Assert.True(run.IsFinished);
    }

    [Fact]
    public async Task Run_page_can_be_left_while_the_backup_keeps_running()
    {
        var gate = new TaskCompletionSource<RunOutcome>();
        var doneCalls = 0;
        var run = new RunViewModel(_app.Shell, "Photos → Disk", "s", "t", (_, _) => gate.Task, onDone: () => doneCalls++);
        _app.Shell.Show(run);
        var running = run.StartAsync();

        run.BackCommand.Execute(null);

        var dashboard = _app.Shell.Dashboard;
        Assert.Same(dashboard, _app.Shell.CurrentPage);
        Assert.Same(run, dashboard.ActiveRun);
        Assert.False(running.IsCompleted);
        Assert.StartsWith("Backing up Photos → Disk", run.StatusText, StringComparison.Ordinal);

        dashboard.ShowRunCommand.Execute(null);
        Assert.Same(run, _app.Shell.CurrentPage);

        // It finishes while the user is on the dashboard: the result stays reachable from there.
        run.BackCommand.Execute(null);
        gate.SetResult(new RunOutcome(ExecutionOutcome.Completed, null, 1, 0, 0, 0, 0, 0, 0, 0, 1, [], [], null, TimeSpan.FromSeconds(1)));
        await running;
        Assert.False(run.BackCommand.CanExecute(null));
        Assert.Same(run, dashboard.ActiveRun);
        Assert.Equal("Photos → Disk: Completed", run.StatusText);

        dashboard.ShowRunCommand.Execute(null);
        run.DoneCommand.Execute(null);

        Assert.Null(dashboard.ActiveRun);
        Assert.Equal(1, doneCalls);
    }

    [Fact]
    public void A_new_run_page_replaces_the_one_the_user_left()
    {
        var first = new RunViewModel(_app.Shell, "first", "s", "t", (_, _) => new TaskCompletionSource<RunOutcome>().Task);
        var second = new RunViewModel(_app.Shell, "second", "s", "t", (_, _) => new TaskCompletionSource<RunOutcome>().Task);
        _app.Shell.Show(first);
        _ = first.StartAsync();
        first.BackCommand.Execute(null);

        _app.Shell.Show(second);
        _ = second.StartAsync();
        first.DoneCommand.Execute(null);

        Assert.Same(second, _app.Shell.Dashboard.ActiveRun);
    }

    [Fact]
    public async Task One_time_run_leaves_no_files_of_its_own()
    {
        _app.Source.File("a.txt", "a");
        var target = _app.Create("one-time-target");
        var oneTime = new OneTimeRunViewModel(_app.Shell) { SourcePath = _app.Source.Root, TargetPath = target };

        await oneTime.AnalyzeCommand.ExecuteAsync(null);
        var diff = await _app.WaitForPageAsync<DiffViewModel>();
        await diff.RunCommand.ExecuteAsync(null);
        var run = await _app.WaitForPageAsync<RunViewModel>(r => r.IsFinished);

        Assert.Equal(ExecutionOutcome.Completed, run.Outcome!.Outcome);
        Assert.Equal("a", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, ".amberkeep")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_app.AppDirectory));
        Assert.Contains("Result:  Completed", Amberkeep.App.Services.ReportBuilder.Text(run.CreateReport()), StringComparison.Ordinal);
    }
}
