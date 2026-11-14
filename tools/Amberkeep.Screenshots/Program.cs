// Development tool: renders every screen with sample data to PNG files (light and dark theme).
// Usage: dotnet run --project tools/Amberkeep.Screenshots -- [output folder]   (default: sandbox/screenshots)
using Amberkeep.App;
using Amberkeep.App.Services;
using Amberkeep.App.ViewModels;
using Amberkeep.App.Views;
using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;

var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("sandbox", "screenshots"));
Directory.CreateDirectory(output);
var data = Path.GetFullPath(Path.Combine("sandbox", "screenshot-data", DateTime.Now.ToString("yyyyMMdd-HHmmss")));

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var sample = SampleData.Create(data);

foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
{
    Application.Current!.RequestedThemeVariant = variant;

    var main = new MainViewModel(sample.Services, new SilentDialogs());
    Pump(main.Dashboard.RefreshAsync());
    Render(main, main.Dashboard, $"01-dashboard-{suffix}");

    var empty = new MainViewModel(sample.EmptyServices, new SilentDialogs());
    Pump(empty.Dashboard.RefreshAsync());
    Render(empty, empty.Dashboard, $"02-dashboard-empty-{suffix}");

    Render(main, new JobEditorViewModel(main, sample.Services.Config.Jobs[0]), $"03-editor-{suffix}", height: 1800);
    Render(main, new DiffViewModel(main, sample.Diff, "Photos → External HDD", false, _ => Task.CompletedTask), $"04-diff-{suffix}");
    Render(main, new BusyViewModel("Analyzing", "Photos → External HDD") { Status = "Scanning…  12,408 items in the source, 11,950 on the target" }, $"05-busy-{suffix}");

    var running = new RunViewModel(main, "Photos → External HDD", sample.Source, sample.Target, (_, _) => new TaskCompletionSource<RunOutcome>().Task);
    _ = running.StartAsync();
    running.Percent = 42;
    running.IsIndeterminate = false;
    running.OperationText = "Copying";
    running.CurrentPath = "2024/Holiday/IMG_2041.JPG";
    running.CountText = "1,204 of 2,880 changes";
    running.SpeedText = "84.2 MB/s  ·  about 3 min 20 s left";
    Render(main, running, $"06-run-progress-{suffix}");

    var finished = new RunViewModel(main, "Photos → External HDD", sample.Source, sample.Target, (_, _) => Task.FromResult(sample.Outcome));
    Pump(finished.StartAsync());
    Render(main, finished, $"07-run-result-{suffix}");

    Render(main, new OneTimeRunViewModel(main) { SourcePath = sample.Source, TargetPath = sample.Target }, $"08-one-time-{suffix}");

    var history = new HistoryViewModel(main);
    Pump(history.LoadAsync());
    Pump(Task.Delay(300));
    Render(main, history, $"10-history-{suffix}");

    var photos = sample.Services.Config.Jobs[0];
    var archive = new ArchiveViewModel(main, photos, photos.Targets[0], sample.Target);
    Pump(archive.LoadAsync());
    Pump(Task.Delay(300));
    archive.Files.Take(2).ToList().ForEach(f => f.IsSelected = true);
    Render(main, archive, $"11-archive-{suffix}");

    Render(main, new SettingsViewModel(main), $"12-settings-{suffix}", height: 1350);

    var photosJob = sample.Services.Config.Jobs[0];
    sample.Services.PendingReviews.Add(new PendingReview(photosJob, photosJob.Targets[0], 7, DateTimeOffset.UtcNow));
    Pump(main.Dashboard.RefreshAsync());
    Render(main, main.Dashboard, $"13-dashboard-review-{suffix}");
    sample.Services.PendingReviews.Remove(photosJob.Targets[0].Id);

    var toast = new Amberkeep.App.Platform.NotificationWindow("Photos → External HDD: 7 changes", "Review the changes to run the backup.", null);
    toast.Show();
    for (var i = 0; i < 5; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    toast.CaptureRenderedFrame()?.Save(Path.Combine(output, $"14-notification-{suffix}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    toast.Close();

    main.Dialog = new ConfirmDialogViewModel(
        "Delete 'Photos'?",
        "The backup is removed from Amberkeep together with its history. Backed-up files stay where they are.",
        "Delete", "Cancel", destructive: true,
        [new DialogOption("Also remove Amberkeep files (marker) from connected targets", true), new DialogOption("Also delete archived old versions on connected targets")]);
    Render(main, main.Dashboard, $"09-dialog-{suffix}");
    main.Dialog = null;
}

// Tray icon states side by side (4x scale).
var strip = new Avalonia.Controls.StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 16, Margin = new Thickness(16), Background = Avalonia.Media.Brushes.White };
foreach (var state in new[]
{
    new ActivityState(ActivityKind.Idle, "", 0, false), new ActivityState(ActivityKind.Analyzing, "", 0, false),
    new ActivityState(ActivityKind.Running, "", 30, false), new ActivityState(ActivityKind.Running, "", 75, false),
    new ActivityState(ActivityKind.Idle, "", 0, true),
})
{
    strip.Children.Add(new Avalonia.Controls.Image { Source = Amberkeep.App.Platform.TrayIconRenderer.RenderBitmap(state), Width = 64, Height = 64 });
}

var iconWindow = new Avalonia.Controls.Window { Content = strip, SizeToContent = Avalonia.Controls.SizeToContent.WidthAndHeight };
iconWindow.Show();
for (var i = 0; i < 5; i++)
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
}

iconWindow.CaptureRenderedFrame()?.Save(Path.Combine(output, "15-tray-icons.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

Console.WriteLine($"Screenshots written to {output}");
return 0;

void Render(MainViewModel main, object page, string name, int width = 1120, int height = 780)
{
    main.CurrentPage = page;
    var window = new MainWindow { DataContext = main, Width = width, Height = height };
    window.Show();
    for (var i = 0; i < 5; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    var frame = window.CaptureRenderedFrame();
    frame?.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    window.Close();
}

static void Pump(Task task)
{
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (!task.IsCompleted && DateTime.UtcNow < deadline)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(10);
    }

    task.GetAwaiter().GetResult();
}

internal sealed class FakeVolumes(params string[] roots) : IVolumeProvider
{
    public IReadOnlyList<string> GetVolumeRoots() => roots;
}

internal sealed class SilentDialogs : IDialogService
{
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task<string?> PickSaveFileAsync(string title, string suggestedFileName) => Task.FromResult<string?>(null);

    public Task<string?> PickOpenFileAsync(string title) => Task.FromResult<string?>(null);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool destructive = false) => Task.FromResult(false);

    public Task<IReadOnlyList<bool>?> ConfirmWithOptionsAsync(
        string title, string message, string confirmText, IReadOnlyList<DialogOption> options, bool destructive = false) =>
        Task.FromResult<IReadOnlyList<bool>?>(null);

    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

    public void OpenFolder(string path)
    {
    }
}

/// <summary>Builds jobs, targets, history and a diff with every category in a sandbox folder.</summary>
internal sealed record SampleData(AppServices Services, AppServices EmptyServices, DiffResult Diff, RunOutcome Outcome, string Source, string Target)
{
    public static SampleData Create(string root)
    {
        var source = Dir(root, "source");
        var volumeA = Dir(root, "volA");
        var volumeB = Dir(root, "volB");
        var app = Dir(root, "app");
        var time = TimeProvider.System;
        var services = AppServices.Create(app, PhysicalFileSystem.Instance, new FakeVolumes(volumeA, volumeB), time);

        var t0 = DateTime.UtcNow.AddDays(-2);
        void Write(string folder, string relative, string content, DateTime when)
        {
            var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, when);
        }

        for (var i = 0; i < 6; i++)
        {
            Write(source, $"2024/Holiday/IMG_20{i:D2}.JPG", new string('x', 2_400_000 + i * 1000), t0);
        }

        Write(source, "2024/Holiday/IMG_2041.JPG", new string('x', 3_100_000), t0.AddDays(1));
        Write(source, "2025/Family/birthday.mp4", new string('v', 9_000_000), t0);
        Write(source, "2025/Family/notes.txt", "notes", t0);
        Write(source, "Albums/Best of.pdf", new string('p', 800_000), t0.AddDays(1));
        Write(source, "Edited on target.docx", "short", t0);

        // Photos: one target that is backed up once, one that is not connected.
        var photos = new JobConfig
        {
            Name = "Photos", SourcePath = source, Archive = new ArchiveSettings { Enabled = true },
            Trigger = new TriggerConfig { OnTargetConnected = true, Schedule = new ScheduleConfig { Kind = ScheduleKind.Daily, Time = new TimeOnly(20, 0) } },
        };
        var external = services.Registry.AddTarget(services.Config, photos, Path.Combine(volumeA, "Backup", "Photos"), "External HDD");
        photos = photos.WithTarget(external);
        var old = services.Registry.AddTarget(services.Config.WithJob(photos), photos, Path.Combine(volumeB, "Backup", "Photos"), "Old HDD");
        photos = photos.WithTarget(old);
        services.Save(services.Config.WithJob(photos));

        var prepared = services.Runner.AnalyzeAsync(photos, external).GetAwaiter().GetResult();
        services.Runner.ExecuteAsync(prepared, prepared.Diff!.Items.ToHashSet(), approved: true).GetAwaiter().GetResult();
        Directory.Move(Path.Combine(volumeB, "Backup"), Path.Combine(root, "unplugged"));

        // Changes since the last backup.
        var target = external.LastKnownPath;
        Write(source, "2024/Holiday/IMG_2041.JPG", new string('y', 3_200_000), t0.AddDays(2));
        Write(source, "2025/New trip/IMG_3001.JPG", new string('n', 2_700_000), t0.AddDays(2));
        Write(source, "2025/New trip/IMG_3002.JPG", new string('n', 2_750_000), t0.AddDays(2));
        File.Delete(Path.Combine(source, "2025", "Family", "notes.txt"));
        Write(target, "Edited on target.docx", "edited on the target later", t0.AddDays(3));
        Write(target, "Old stuff/readme.txt", "old", t0);

        // Documents: target folder exists, but holds a different disk's marker.
        var documentsSource = Dir(root, "documents");
        var documents = new JobConfig { Name = "Documents", SourcePath = documentsSource, RequireApproval = false };
        var docsTarget = services.Registry.AddTarget(services.Config, documents, Path.Combine(volumeA, "Docs"), "USB stick");
        File.Delete(Path.Combine(docsTarget.LastKnownPath, ".amberkeep", "target.json"));
        services.Save(services.Config.WithJob(documents.WithTarget(docsTarget)));

        // Archive snapshots from earlier runs.
        foreach (var (daysAgo, files) in new[] { (1, new[] { "2024/Holiday/IMG_2041.JPG", "2025/Family/notes.txt", "Old/plan.docx" }), (9, new[] { "Albums/Best of.pdf" }) })
        {
            var snapshot = Path.Combine(target, ".amberkeep", "archive", Amberkeep.Core.Execution.ArchiveCleaner.SnapshotName(DateTimeOffset.Now.AddDays(-daysAgo)));
            foreach (var file in files)
            {
                Write(snapshot, file, new string('a', 1_200_000), t0);
            }
        }

        // A second, less successful run in the history.
        services.History.Add(new Amberkeep.Storage.History.RunRecord
        {
            RunId = Guid.NewGuid(), JobId = photos.Id, JobName = "Photos", TargetId = external.Id, TargetName = "External HDD",
            SourcePath = source, TargetPath = target, StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-30), FinishedUtc = DateTimeOffset.UtcNow.AddMinutes(-29),
            Outcome = ExecutionOutcome.CompletedWithErrors, CopiedFiles = 3, RemovedFiles = 1, CopiedBytes = 8_400_000,
            Failures = [new ExecutionFailure("2025/Family/birthday.mp4", ExecutionOperation.Copy, "The file is being used by another process.")],
        }, 100);

        var diff = new DiffEngine().AnalyzeAsync(new DiffRequest(source, target, photos.ToDiffOptions())).GetAwaiter().GetResult();

        var outcome = new RunOutcome(
            ExecutionOutcome.CompletedWithErrors, null, 2, 1, 1, 0, 1, 0, 0, 2, 8_650_000,
            [new ExecutionFailure("2025/Family/birthday.mp4", ExecutionOperation.Copy, "The process cannot access the file because it is being used by another process.")],
            ["Folder kept because it is not empty: Old stuff"],
            Path.Combine(target, ".amberkeep", "archive", "2026-11-14_101500"),
            TimeSpan.FromSeconds(47));

        var empty = AppServices.Create(Dir(root, "empty-app"), PhysicalFileSystem.Instance, new FakeVolumes(), time);
        return new SampleData(services, empty, diff, outcome, source, target);
    }

    private static string Dir(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
