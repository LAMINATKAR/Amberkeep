using System.Security;
using Amberkeep.App.Services;
using Amberkeep.App.ViewModels;
using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Core.Paths;
using Amberkeep.Storage.Cleanup;
using Amberkeep.Storage.Config;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.App;

/// <summary>Fixes from the second code review (1.0.2).</summary>
public sealed class SecondReviewFixTests : IDisposable
{
    private readonly AppFixture _app;
    private readonly ThrowingAutostart _autostart = new();

    public SecondReviewFixTests() => _app = new AppFixture(autostart: _autostart);

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Folder_renamed_only_in_case_converges_for_a_case_sensitive_backup()
    {
        // The source folder was renamed "docs" -> "Docs"; the job compares names with letter case.
        using var fx = new MirrorFixture();
        fx.Source.File("Docs/a.txt", "x");
        fx.Target.File("docs/a.txt", "x");
        var options = new DiffOptions { CaseSensitivity = CaseSensitivity.Sensitive };

        MirrorFixture.AssertCompleted(await fx.MirrorAsync(await fx.AnalyzeAsync(options)));

        Assert.Equal(["Docs"], Directory.GetDirectories(fx.Target.Root).Select(Path.GetFileName));
        Assert.Empty((await fx.AnalyzeAsync(options)).Items);
    }

    [Fact]
    public void Clean_up_everything_finishes_when_the_autostart_entry_cannot_be_removed()
    {
        var report = _app.Services.Cleanup.CleanUpEverything(_app.Services.Config, cleanTargets: false, removeArchives: false);

        Assert.Contains(report.Errors, e => e.Contains("Autostart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clean_up_everything_always_releases_the_gate_and_closes()
    {
        var exited = false;
        _app.Shell.ExitRequested += (_, _) => exited = true;
        _app.Dialogs.OptionAnswers.Enqueue([false, false]);

        await new SettingsViewModel(_app.Shell).CleanUpEverythingCommand.ExecuteAsync(null);

        Assert.True(exited);
        Assert.False(_app.Services.Gate.IsBusy);
        Assert.False(_app.Services.Gate.TryEnter()); // nothing new starts before the app has closed
        Assert.Contains(_app.Dialogs.Messages, m => m.StartsWith("Some files could not be removed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_page_shows_a_result_after_an_unexpected_error()
    {
        var run = new RunViewModel(_app.Shell, "t", "s", "t", (_, _) => throw new NotSupportedException("unexpected"));

        await run.StartAsync();

        Assert.False(run.IsRunning);
        Assert.Equal(ExecutionOutcome.Refused, run.Outcome!.Outcome);
    }

    [Fact]
    public async Task Removing_a_target_in_the_editor_also_removes_its_waiting_review()
    {
        var (job, target) = _app.SavedJob();
        _app.Services.PendingReviews.Add(new PendingReview(job, target, 3, DateTimeOffset.UtcNow));
        var editor = new JobEditorViewModel(_app.Shell, job);
        _app.Dialogs.OptionAnswers.Enqueue([false, false]);
        await editor.RemoveTargetCommand.ExecuteAsync(editor.Targets.Single());

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.False(_app.Services.PendingReviews.Contains(target.Id));
    }

    [Fact]
    public async Task Review_click_does_not_leave_a_page_while_something_runs()
    {
        var (job, target) = _app.SavedJob();
        var review = new PendingReview(job, target, 3, DateTimeOffset.UtcNow);
        _app.Services.PendingReviews.Add(review);
        var history = new HistoryViewModel(_app.Shell);
        _app.Shell.Show(history);
        Assert.True(_app.Services.Gate.TryEnter()); // e.g. a restore on the archive page

        _app.Shell.OpenReview(review);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Same(history, _app.Shell.CurrentPage);
        Assert.True(_app.Services.PendingReviews.Contains(target.Id));
        _app.Services.Gate.Exit();
    }

    [Fact]
    public void Changed_schedule_starts_counting_when_it_is_saved()
    {
        var (job, _) = _app.SavedJob();
        var editor = new JobEditorViewModel(_app.Shell, job);
        editor.ScheduleKind = editor.ScheduleChoices.Single(c => c.Value == ScheduleKind.Daily);

        var savedJob = editor.BuildJob();
        var saved = savedJob.Trigger.Schedule;

        Assert.NotNull(saved.SinceUtc);
        var unchanged = new JobEditorViewModel(_app.Shell, savedJob).BuildJob().Trigger.Schedule;
        Assert.Equal(saved.SinceUtc, unchanged.SinceUtc);
    }

    private sealed class ThrowingAutostart : IAutostart
    {
        public bool IsEnabled => false;

        public void Enable() => throw new SecurityException("Blocked by policy.");

        public void Remove() => throw new SecurityException("Blocked by policy.");
    }
}
