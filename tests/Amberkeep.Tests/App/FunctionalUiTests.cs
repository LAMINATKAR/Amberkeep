using Amberkeep.App.Services;
using Amberkeep.App.ViewModels;
using Amberkeep.App.Views;
using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;
using Amberkeep.Tests.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amberkeep.Tests.App;

/// <summary>
/// The real window, views and overlay dialogs driven by mouse clicks (headless). Only the operating system's file
/// pickers and file manager are replaced, because they do not exist without a desktop.
/// </summary>
public sealed class FunctionalUiTests : IDisposable
{
    private readonly AppFixture _app = new();
    private readonly MirrorFixture _fx = new();
    private readonly RealDialogs _dialogs;

    public FunctionalUiTests()
    {
        _dialogs = new RealDialogs(_app.Shell);
        _app.Shell.Dialogs = _dialogs;
    }

    public void Dispose()
    {
        _fx.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task Right_click_on_a_change_shows_it_in_the_file_manager()
    {
        _fx.Source.File("docs/new.txt");
        _fx.Target.File("docs/old.txt", "old");
        var diff = await _fx.AnalyzeAsync();

        await HeadlessApp.Session.Dispatch(
            () =>
            {
                var page = new DiffViewModel(_app.Shell, diff, "Test", archiveEnabled: false, _ => Task.CompletedTask);
                page.ExpandAllCommand.Execute(null);
                _app.Shell.CurrentPage = page;
                var window = Show();

                var added = Row(window, "new.txt");
                Click(added, MouseButton.Right);
                var menu = added.ContextMenu!;
                Assert.True(menu.IsOpen);
                var items = menu.Items.OfType<MenuItem>().ToList();
                Assert.Equal(
                    ["Open source file", "Open target file", "Show in source folder", "Show in target folder"],
                    items.Select(i => i.Header as string));
                Assert.True(items[0].IsEffectivelyEnabled);
                Assert.False(items[1].IsEffectivelyEnabled);
                Assert.True(items[2].IsEffectivelyEnabled);
                Assert.False(items[3].IsEffectivelyEnabled);
                Click(items[2]);
                Assert.False(menu.IsOpen);

                var removed = Row(window, "old.txt");
                Click(removed, MouseButton.Right);
                var removedItems = removed.ContextMenu!.Items.OfType<MenuItem>().ToList();
                Assert.False(removedItems[0].IsEffectivelyEnabled);
                Assert.True(removedItems[1].IsEffectivelyEnabled);
                Assert.False(removedItems[2].IsEffectivelyEnabled);
                Click(removedItems[3]);
                Click(removed, MouseButton.Right);
                Click(removedItems[1]);

                Assert.Equal(
                    [Path.Combine(_fx.Source.Root, "docs", "new.txt"), Path.Combine(_fx.Target.Root, "docs", "old.txt")],
                    _dialogs.Revealed);
                Assert.Equal([Path.Combine(_fx.Target.Root, "docs", "old.txt")], _dialogs.Opened);

                var folder = Row(window, "docs");
                Click(folder, MouseButton.Right);
                Assert.Equal(
                    ["Show in source folder", "Show in target folder"],
                    folder.ContextMenu!.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header as string));
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Import_shows_the_summary_in_the_window_and_imports_after_confirming()
    {
        var (job, _) = _app.SavedJob();
        var file = Path.Combine(_app.BasePath, "backups.json");
        ConfigTransfer.Export(PhysicalFileSystem.Instance, _app.Services.Config, [job.Id], file);
        _app.Services.Save(new AppConfig());
        _dialogs.OpenFiles.Enqueue(file);

        await HeadlessApp.Session.Dispatch(
            async () =>
            {
                _app.Shell.CurrentPage = new SettingsViewModel(_app.Shell);
                var window = Show();

                Click(ButtonWithText(window, "Import…"));
                await Until(() => _app.Shell.Dialog is not null);
                var summary = Texts(window);
                Assert.Contains("Import 1 backup?", summary);
                Assert.Contains(summary, t => t.Contains($"From: {job.SourcePath}", StringComparison.Ordinal)
                                              && t.Contains("Asks before every run: yes", StringComparison.Ordinal));
                Assert.Empty(_app.Services.Config.Jobs);

                Click(ButtonWithText(window, "Import"));
                await Until(() => _app.Shell.Dialog is { Title: "Import finished" });
                Assert.Equal(job.Id, Assert.Single(_app.Services.Config.Jobs).Id);

                Click(ButtonWithText(window, "OK"));
                await Until(() => _app.Shell.Dialog is null);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Analyze_review_and_run_from_the_dashboard_copies_the_files()
    {
        var (_, target) = _app.SavedJob();
        _app.Source.File("a.txt", "first").File("sub/b.txt", "second");
        await _app.Shell.Dashboard.RefreshAsync();

        await HeadlessApp.Session.Dispatch(
            async () =>
            {
                _app.Shell.CurrentPage = _app.Shell.Dashboard;
                var window = Show();

                Click(ButtonWithText(window, "Analyze"));
                await Until(() => _app.Shell.CurrentPage is DiffViewModel);
                Pump();
                Click(ButtonWithText(window, "Approve & run"));
                await Until(() => _app.Shell.CurrentPage is RunViewModel { Outcome: not null });

                Assert.Equal("first", File.ReadAllText(Path.Combine(target.LastKnownPath, "a.txt")));
                Assert.Equal("second", File.ReadAllText(Path.Combine(target.LastKnownPath, "sub", "b.txt")));
                Pump();
                Click(ButtonWithText(window, "Done"));
                await Until(() => _app.Shell.CurrentPage is DashboardViewModel);
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dragging_a_card_by_its_grip_changes_and_saves_the_order_and_esc_cancels()
    {
        _app.SavedJob("Photos");
        _app.SavedJob("Documents");
        _app.SavedJob("Music");

        await HeadlessApp.Session.Dispatch(
            async () =>
            {
                _app.Shell.ShowDashboard();
                var window = Show();
                await Until(() => _app.Shell.Dashboard.Jobs.Count == 3);

                // Drag Music above Photos.
                var top = Card(window, "Photos");
                var start = Center(Grip(window, "Music"), window);
                window.MouseMove(start);
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(start + new Vector(0, -2));
                Pump();
                Assert.Equal(["Photos", "Documents", "Music"], _app.Shell.Dashboard.Jobs.Select(j => j.Name));
                var above = top.TranslatePoint(new Point(40, 5), window)!.Value;
                window.MouseMove(new Point(start.X, above.Y));
                Pump();
                Assert.Equal(["Music", "Photos", "Documents"], _app.Shell.Dashboard.Jobs.Select(j => j.Name));
                Assert.True(_app.Shell.Dashboard.Jobs[0].IsDragging);
                Assert.Equal(["Photos", "Documents", "Music"], _app.Services.Config.Jobs.Select(j => j.Name));
                window.MouseUp(new Point(start.X, above.Y), MouseButton.Left);
                Pump();

                Assert.False(_app.Shell.Dashboard.Jobs[0].IsDragging);
                Assert.Equal(["Music", "Photos", "Documents"], _app.Services.Config.Jobs.Select(j => j.Name));

                // Esc in the middle of dragging puts the card back.
                var grip = Center(Grip(window, "Documents"), window);
                window.MouseMove(grip);
                window.MouseDown(grip, MouseButton.Left);
                var first = Card(window, "Music").TranslatePoint(new Point(40, 5), window)!.Value;
                window.MouseMove(new Point(grip.X, first.Y));
                Pump();
                Assert.Equal(["Documents", "Music", "Photos"], _app.Shell.Dashboard.Jobs.Select(j => j.Name));
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.MouseUp(new Point(grip.X, first.Y), MouseButton.Left);
                Pump();

                Assert.Equal(["Music", "Photos", "Documents"], _app.Shell.Dashboard.Jobs.Select(j => j.Name));
                Assert.Equal(["Music", "Photos", "Documents"], _app.Services.Config.Jobs.Select(j => j.Name));
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    private static Border Grip(Window window, string job) =>
        window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("drag-handle") && b.DataContext is JobCardViewModel card && card.Name == job);

    private static Border Card(Window window, string job) =>
        window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("card") && b.DataContext is JobCardViewModel card && card.Name == job);

    private static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private MainWindow Show()
    {
        var window = new MainWindow { DataContext = _app.Shell, Width = 1120, Height = 780 };
        window.Show();
        Pump();
        return window;
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the UI.");
            Pump();
            await Task.Delay(20);
        }

        Pump();
    }

    /// <summary>Presses and releases a mouse button in the middle of a control, like a user would.</summary>
    private static void Click(Control control, MouseButton button = MouseButton.Left)
    {
        var top = TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("The control is not shown.");
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)
                    ?? throw new InvalidOperationException("The control is not laid out.");
        top.MouseMove(point);
        top.MouseDown(point, button);
        top.MouseUp(point, button);
        Pump();
    }

    private static Grid Row(Window window, string name) =>
        window.GetVisualDescendants().OfType<Grid>()
            .Single(g => g.ContextMenu is not null && g.DataContext is DiffNodeViewModel node && node.Name == name);

    private static Button ButtonWithText(Window window, string text) =>
        window.GetVisualDescendants().OfType<Button>()
            .Last(b => b.IsEffectivelyVisible && b.Content as string == text);

    private static List<string> Texts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text is not null).Select(t => t.Text!).ToList();

    /// <summary>The app's overlay dialogs; file pickers answer from a queue and the file manager is recorded.</summary>
    private sealed class RealDialogs(MainViewModel host) : IDialogService
    {
        private readonly OverlayDialogService _overlay = new(host, () => null);

        public Queue<string?> OpenFiles { get; } = new();

        public List<string> Revealed { get; } = [];

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedFileName) => Task.FromResult<string?>(null);

        public Task<string?> PickOpenFileAsync(string title) => Task.FromResult(OpenFiles.TryDequeue(out var file) ? file : null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool destructive = false) =>
            _overlay.ConfirmAsync(title, message, confirmText, destructive);

        public Task<IReadOnlyList<bool>?> ConfirmWithOptionsAsync(
            string title, string message, string confirmText, IReadOnlyList<DialogOption> options, bool destructive = false) =>
            _overlay.ConfirmWithOptionsAsync(title, message, confirmText, options, destructive);

        public Task ShowMessageAsync(string title, string message) => _overlay.ShowMessageAsync(title, message);

        public Task ShowTextAsync(string title, string text) => _overlay.ShowTextAsync(title, text);

        public void OpenFolder(string path)
        {
        }

        public void RevealInFileManager(string path) => Revealed.Add(path);

        public List<string> Opened { get; } = [];

        public void OpenFile(string path) => Opened.Add(path);
    }
}
