using Amberkeep.App.Services;
using Amberkeep.App.ViewModels;
using Amberkeep.Storage.Config;

namespace Amberkeep.Tests.App;

/// <summary>Fixes from the code review of 1.0 in the app: stale jobs, reviews, quitting, concurrency.</summary>
public sealed class ReliabilityFixTests : IDisposable
{
    private readonly AppFixture _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Gate_stop_cancels_the_running_work_waits_for_it_and_lets_nothing_new_start()
    {
        var gate = new RunGate();
        Assert.True(gate.TryEnter());
        var token = gate.Token;
        var work = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken); // cleaning up
            }
            finally
            {
                gate.Exit();
            }
        }, TestContext.Current.CancellationToken);

        await gate.StopAsync(TimeSpan.FromSeconds(10));

        Assert.True(work.IsCompleted);
        Assert.False(gate.TryEnter());
    }

    [Fact]
    public async Task Gate_stop_without_work_returns_at_once()
    {
        var gate = new RunGate();

        await gate.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.False(gate.TryEnter());
    }

    [Fact]
    public async Task Gate_released_for_closing_lets_quit_finish_at_once()
    {
        // Clean up everything holds the gate, then lets go of it right before closing.
        var gate = new RunGate();
        Assert.True(gate.TryEnter());

        gate.ExitAndStop();

        await gate.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(gate.TryEnter());
    }

    [Fact]
    public async Task Activity_state_stays_consistent_when_updated_from_several_threads()
    {
        var activity = new ActivityMonitor();
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 500; n++)
            {
                activity.Analyzing("x");
                activity.SetReviewsWaiting(n % 2 == 0);
                activity.Running("x", n % 100);
                activity.Idle();
            }
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        activity.SetReviewsWaiting(false);
        activity.Idle();

        Assert.Equal(new ActivityState(ActivityKind.Idle, "Amberkeep", 0, false), activity.State);
        activity.SetReviewsWaiting(true);
        Assert.True(activity.State.NeedsAttention);
        Assert.Equal("Amberkeep – needs your attention", activity.State.Text);
    }

    [Fact]
    public async Task Stale_card_runs_with_the_saved_settings()
    {
        // The card was built when review was off; then "Review changes before every run" was switched on and saved.
        var (stale, target) = _app.SavedJob(requireApproval: false);
        _app.Services.Update(c => c.WithJob(stale with { RequireApproval = true }));
        _app.Source.File("new.txt");

        await new JobFlow(_app.Shell).StartAsync(stale, target, runWithoutReview: true);

        await _app.WaitForPageAsync<DiffViewModel>();
    }

    [Fact]
    public async Task Removed_job_does_not_run_from_an_old_card()
    {
        var (job, target) = _app.SavedJob();
        _app.Services.Update(c => c.WithoutJob(job.Id));

        await new JobFlow(_app.Shell).StartAsync(job, target, runWithoutReview: true);

        Assert.IsType<DashboardViewModel>(_app.Shell.CurrentPage);
        Assert.Empty(_app.Services.History.List());
    }

    [Fact]
    public async Task Review_click_does_not_leave_the_job_editor_and_keeps_the_review()
    {
        var (job, target) = _app.SavedJob();
        var editor = new JobEditorViewModel(_app.Shell, job) { Name = "Unsaved change" };
        _app.Shell.Show(editor);
        var review = new PendingReview(job, target, 3, DateTimeOffset.UtcNow);
        _app.Services.PendingReviews.Add(review);

        _app.Shell.OpenReview(review);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Same(editor, _app.Shell.CurrentPage);
        Assert.True(_app.Services.PendingReviews.Contains(target.Id));
    }

    [Fact]
    public async Task Review_click_while_a_backup_runs_keeps_the_review()
    {
        var (job, target) = _app.SavedJob();
        var review = new PendingReview(job, target, 3, DateTimeOffset.UtcNow);
        _app.Services.PendingReviews.Add(review);
        Assert.True(_app.Services.Gate.TryEnter());

        _app.Shell.OpenReview(review);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.True(_app.Services.PendingReviews.Contains(target.Id));
        _app.Services.Gate.Exit();
    }

    [Fact]
    public async Task Review_click_on_the_dashboard_opens_the_review_and_takes_it_out_of_the_bar()
    {
        var (job, target) = _app.SavedJob();
        _app.Source.File("new.txt");
        var review = new PendingReview(job, target, 1, DateTimeOffset.UtcNow);
        _app.Services.PendingReviews.Add(review);
        _app.Shell.ShowDashboard();

        _app.Shell.OpenReview(review);

        await _app.WaitForPageAsync<DiffViewModel>();
        Assert.False(_app.Services.PendingReviews.Contains(target.Id));
    }

    [Fact]
    public async Task Background_run_failure_is_reported_and_does_not_escape()
    {
        var (job, target) = _app.SavedJob(requireApproval: false);
        _app.Services.Update(c => c.WithJob(job with { SourcePath = string.Empty }));
        var notifier = new FakeNotifier();
        var runner = new BackgroundRunner(_app.Services, notifier);

        var result = await runner.RunAsync(job, target, TestContext.Current.CancellationToken);

        Assert.Equal(BackgroundRunResult.CompletedWithProblems, result);
        Assert.True(_app.Services.Activity.State.NeedsAttention);
        Assert.Contains(notifier.Shown, n => n.Title.Contains("failed", StringComparison.Ordinal));
        Assert.False(_app.Services.Gate.IsBusy);
    }

    [Fact]
    public async Task Background_run_of_a_removed_target_does_not_bring_it_back()
    {
        var (job, target) = _app.SavedJob(requireApproval: false);
        _app.Services.Update(c => c.WithJob(job.WithoutTarget(target.Id)));
        var runner = new BackgroundRunner(_app.Services, new FakeNotifier());

        var result = await runner.RunAsync(job, target, TestContext.Current.CancellationToken);

        Assert.Equal(BackgroundRunResult.NotConnected, result);
        Assert.Empty(_app.Services.Config.FindJob(job.Id)!.Targets);
    }

    [Fact]
    public async Task Job_is_not_deleted_while_a_backup_runs()
    {
        var (job, _) = _app.SavedJob();
        _app.Dialogs.OptionAnswers.Enqueue([true, true, true]);
        Assert.True(_app.Services.Gate.TryEnter());

        await _app.Shell.Dashboard.DeleteJobAsync(job);

        Assert.NotNull(_app.Services.Config.FindJob(job.Id));
        Assert.Contains(_app.Dialogs.Messages, m => m.StartsWith("A backup is running", StringComparison.Ordinal));
        _app.Services.Gate.Exit();
    }

    [Fact]
    public async Task Deleting_a_job_saves_first_and_then_removes_its_marker()
    {
        var (job, target) = _app.SavedJob();
        _app.Dialogs.OptionAnswers.Enqueue([true, false, false]);

        await _app.Shell.Dashboard.DeleteJobAsync(job);

        Assert.Null(_app.Services.Config.FindJob(job.Id));
        Assert.Null(new Amberkeep.Storage.AppStore(_app.AppDirectory).Load().Config!.FindJob(job.Id));
        Assert.False(File.Exists(Path.Combine(target.LastKnownPath, ".amberkeep", "target.json")));
    }

    [Fact]
    public async Task Deleted_job_keeps_its_runs_in_history_marked_as_deleted()
    {
        var (job, _) = _app.SavedJob(requireApproval: false);
        var (other, _) = _app.SavedJob(requireApproval: false, name: "Other");
        _app.Source.File("a.txt", "a");
        await _app.Shell.Dashboard.RefreshAsync();
        await _app.Shell.Dashboard.Jobs.Single(c => c.Job.Id == job.Id).Targets[0].RunCommand.ExecuteAsync(null);
        (await _app.WaitForPageAsync<RunViewModel>(r => r.IsFinished)).DoneCommand.Execute(null);
        _app.Dialogs.OptionAnswers.Enqueue([true, false, false]);

        await _app.Shell.Dashboard.DeleteJobAsync(job);

        var history = new HistoryViewModel(_app.Shell);
        await history.LoadAsync();
        var row = Assert.Single(history.Runs);
        Assert.Equal(job.Id, row.Record.JobId);
        Assert.True(row.IsDeleted);
        Assert.Contains(history.Filters, f => f.JobId == job.Id && f.Label.EndsWith("(deleted)", StringComparison.Ordinal));
        Assert.Contains(history.Filters, f => f.JobId == other.Id);
    }

    [Fact]
    public async Task Deleted_job_can_take_its_runs_with_it()
    {
        var (job, _) = _app.SavedJob(requireApproval: false);
        _app.Source.File("a.txt", "a");
        await _app.Shell.Dashboard.RefreshAsync();
        await _app.Shell.Dashboard.Jobs[0].Targets[0].RunCommand.ExecuteAsync(null);
        (await _app.WaitForPageAsync<RunViewModel>(r => r.IsFinished)).DoneCommand.Execute(null);
        _app.Dialogs.OptionAnswers.Enqueue([true, false, true]);

        await _app.Shell.Dashboard.DeleteJobAsync(job);

        Assert.Empty(_app.Services.History.List());
    }
}
