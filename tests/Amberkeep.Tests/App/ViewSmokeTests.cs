using Amberkeep.App;
using Amberkeep.App.ViewModels;
using Amberkeep.App.Views;
using Amberkeep.Core.Execution;
using Amberkeep.Tests.Infrastructure;
using Avalonia;
using Avalonia.Headless;

namespace Amberkeep.Tests.App;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Amberkeep.App.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

    private static readonly Lazy<HeadlessUnitTestSession> LazySession = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

    public static HeadlessUnitTestSession Session => LazySession.Value;
}

/// <summary>Every view can be created, bound to its view model and laid out without errors.</summary>
public sealed class ViewSmokeTests : IDisposable
{
    private readonly AppFixture _app = new();
    private readonly MirrorFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task All_pages_render()
    {
        var (job, _) = _app.SavedJob();
        _fx.Source.File("a/b.txt").File("c.txt");
        _fx.Target.File("old.txt");
        var diff = await _fx.AnalyzeAsync();
        await _app.Shell.Dashboard.RefreshAsync();

        var outcome = new RunOutcome(
            ExecutionOutcome.CompletedWithErrors, null, 1, 1, 1, 1, 1, 1, 1, 1, 1024,
            [new ExecutionFailure("x", ExecutionOperation.Copy, "Locked")], ["Note"], "archive", TimeSpan.FromSeconds(3));
        var finished = new RunViewModel(_app.Shell, "t", "s", "t", (_, _) => Task.FromResult(outcome));
        await finished.StartAsync();

        var history = new HistoryViewModel(_app.Shell);
        await history.LoadAsync();
        var target = job.Targets[0];
        Directory.CreateDirectory(Path.Combine(target.LastKnownPath, ".amberkeep", "archive", ArchiveCleaner.SnapshotName(DateTimeOffset.Now), "x"));
        var archive = new ArchiveViewModel(_app.Shell, job, target, target.LastKnownPath);
        await archive.LoadAsync();

        object[] pages =
        [
            _app.Shell.Dashboard,
            new JobEditorViewModel(_app.Shell, job),
            new JobEditorViewModel(_app.Shell, null),
            new DiffViewModel(_app.Shell, diff, "t", false, _ => Task.CompletedTask),
            new BusyViewModel("Analyzing", "x"),
            new RunViewModel(_app.Shell, "t", "s", "t", (_, _) => new TaskCompletionSource<RunOutcome>().Task),
            finished,
            new OneTimeRunViewModel(_app.Shell),
            history,
            archive,
            new SettingsViewModel(_app.Shell),
        ];

        await HeadlessApp.Session.Dispatch(
            () =>
            {
                foreach (var page in pages)
                {
                    _app.Shell.CurrentPage = page;
                    _app.Shell.Dialog = new ConfirmDialogViewModel("Title", "Message", "OK", "Cancel", destructive: true, [new("Option")]);
                    var window = new MainWindow { DataContext = _app.Shell, Width = 1100, Height = 760 };
                    window.Show();
                    window.UpdateLayout();
                    Assert.True(window.IsVisible, page.GetType().Name);
                    window.Close();
                }
            },
            TestContext.Current.CancellationToken);
    }
}
