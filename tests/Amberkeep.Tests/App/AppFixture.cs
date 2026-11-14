using Amberkeep.App.Services;
using Amberkeep.App.ViewModels;
using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;
using Amberkeep.Tests.Infrastructure;
using Amberkeep.Tests.Storage;

namespace Amberkeep.Tests.App;

/// <summary>Scripted answers for dialogs; records what was shown.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public Queue<string?> Folders { get; } = new();

    public Queue<bool> Confirmations { get; } = new();

    public Queue<IReadOnlyList<bool>?> OptionAnswers { get; } = new();

    public List<string> Messages { get; } = [];

    public Task<string?> PickFolderAsync(string title) => Task.FromResult(Folders.TryDequeue(out var f) ? f : null);

    public Queue<string?> SaveFiles { get; } = new();

    public Queue<string?> OpenFiles { get; } = new();

    public Task<string?> PickSaveFileAsync(string title, string suggestedFileName) =>
        Task.FromResult(SaveFiles.TryDequeue(out var f) ? f : null);

    public Task<string?> PickOpenFileAsync(string title) => Task.FromResult(OpenFiles.TryDequeue(out var f) ? f : null);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool destructive = false)
    {
        Messages.Add(title);
        return Task.FromResult(Confirmations.TryDequeue(out var answer) && answer);
    }

    public Task<IReadOnlyList<bool>?> ConfirmWithOptionsAsync(
        string title, string message, string confirmText, IReadOnlyList<DialogOption> options, bool destructive = false)
    {
        Messages.Add(title);
        return Task.FromResult(OptionAnswers.TryDequeue(out var answer) ? answer : null);
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add($"{title}: {message}");
        return Task.CompletedTask;
    }

    public void OpenFolder(string path)
    {
    }
}

/// <summary>App services over temp folders, with two fake volumes, and a shell with scripted dialogs.</summary>
internal sealed class AppFixture : IDisposable
{
    private readonly TempDirectory _temp = new();

    public AppFixture(TimeProvider? time = null, Amberkeep.Storage.Cleanup.IAutostart? autostart = null)
    {
        AppDirectory = Create("app");
        VolumeA = Create("volA");
        VolumeB = Create("volB");
        Source = new TreeBuilder(System.IO.Path.Combine(_temp.Path, "source"));
        Volumes = new FakeVolumes(VolumeA, VolumeB);
        Services = AppServices.Create(AppDirectory, PhysicalFileSystem.Instance, Volumes, time ?? TimeProvider.System, autostart);
        Shell = new MainViewModel(Services, Dialogs);
    }

    public string BasePath => _temp.Path;

    public string AppDirectory { get; }

    public string VolumeA { get; }

    public string VolumeB { get; }

    public TreeBuilder Source { get; }

    public FakeVolumes Volumes { get; }

    public AppServices Services { get; }

    public FakeDialogs Dialogs { get; } = new();

    public MainViewModel Shell { get; }

    /// <summary>Saves a job with one target at volA/Backup/&lt;name&gt;.</summary>
    public (JobConfig Job, TargetConfig Target) SavedJob(string name = "Photos", bool requireApproval = true)
    {
        var job = new JobConfig { Name = name, SourcePath = Source.Root, RequireApproval = requireApproval };
        var target = Services.Registry.AddTarget(Services.Config, job, System.IO.Path.Combine(VolumeA, "Backup", name), "External HDD");
        job = job.WithTarget(target);
        Services.Save(Services.Config.WithJob(job));
        return (job, target);
    }

    public string Create(string relative)
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(_temp.Path, relative));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Waits until the shell shows a page of type <typeparamref name="T"/> (pages change asynchronously).</summary>
    public async Task<T> WaitForPageAsync<T>(Func<T, bool>? until = null)
        where T : class
    {
        for (var i = 0; i < 500; i++)
        {
            if (Shell.CurrentPage is T page && (until is null || until(page)))
            {
                return page;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Page {typeof(T).Name} not shown; current: {Shell.CurrentPage?.GetType().Name}. Messages: {string.Join(" | ", Dialogs.Messages)}");
    }

    public void Dispose() => _temp.Dispose();
}
