using Amberkeep.App.Services;
using Amberkeep.App.ViewModels;
using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Core.FileSystem;
using Amberkeep.Storage;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.History;

namespace Amberkeep.Tests.App;

/// <summary>History, archive, settings and reports.</summary>
public sealed class Phase5Tests : IDisposable
{
    private readonly AppFixture _app = new();

    public void Dispose() => _app.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Text_and_html_reports_contain_the_run_and_escape_html()
    {
        var report = new RunReport(
            "Photos → <USB> & \"stick\"", "C:\\src", "E:\\dst", DateTimeOffset.Now, "Completed with errors",
            [new StatViewModel("Copied", "2 files")],
            ["a<b>.txt: locked"],
            ["Folder kept"],
            [new RecordedChange("x & y.txt", EntryKind.File, DiffCategory.New)],
            ChangesTruncated: true);

        var text = ReportBuilder.Text(report);
        var html = ReportBuilder.Html(report);

        Assert.Contains("Result:  Completed with errors", text, StringComparison.Ordinal);
        Assert.Contains("x & y.txt", text, StringComparison.Ordinal);
        Assert.Contains("&lt;USB&gt; &amp; &quot;stick&quot;", html, StringComparison.Ordinal);
        Assert.Contains("a&lt;b&gt;.txt", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<USB>", html, StringComparison.Ordinal);
        Assert.Contains("Only the first 1 changes", html, StringComparison.Ordinal);
        Assert.Equal(html, ReportBuilder.Build(report, "r.HTML"));
        Assert.Equal(text, ReportBuilder.Build(report, "r.txt"));
    }

    [Fact]
    public async Task History_lists_runs_filters_by_job_and_exports_a_report()
    {
        var photos = await BackUpOnceAsync("Photos", "photo.jpg");
        await BackUpOnceAsync("Documents", "doc.txt");
        var history = new HistoryViewModel(_app.Shell);

        await history.LoadAsync();
        Assert.Equal(2, history.Runs.Count);

        history.SelectedFilter = history.Filters.Single(f => f.JobId == photos.Id);
        var row = Assert.Single(history.Runs);
        Assert.Same(row, history.SelectedRun);
        await WaitUntilAsync(() => history.Changes.Count > 0);
        Assert.Contains(history.Changes, c => c.Path == "photo.jpg");
        Assert.Contains(history.Stats, s => s.Label == "Copied");

        var path = Path.Combine(_app.BasePath, "report.html");
        _app.Dialogs.SaveFiles.Enqueue(path);
        await history.ExportReportCommand.ExecuteAsync(null);
        Assert.Contains("photo.jpg", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_page_restores_selected_files_and_deletes_snapshots()
    {
        var (job, target) = _app.SavedJob(requireApproval: false);
        _app.Services.Save(_app.Services.Config.WithJob(job with { Archive = new ArchiveSettings { Enabled = true } }));
        job = _app.Services.Config.Jobs.Single();
        _app.Source.File("keep.txt", "v1").File("other.txt", "o");
        await RunAsync(job, target);
        _app.Source.File("keep.txt", "v2 – newer", DateTime.UtcNow);
        await RunAsync(job, target);

        var archive = new ArchiveViewModel(_app.Shell, job, target, target.LastKnownPath);
        await archive.LoadAsync();
        await WaitUntilAsync(() => archive.Files.Count > 0);
        Assert.Single(archive.Snapshots);
        Assert.True(archive.Snapshots[0].IsMeasured);
        Assert.Equal("1 file · 2 B", archive.Snapshots[0].Summary);
        Assert.Equal(["keep.txt"], archive.Files.Select(f => f.Path));

        archive.SelectAllCommand.Execute(null);
        var destination = _app.Create("restored");
        _app.Dialogs.Folders.Enqueue(destination);
        _app.Dialogs.OptionAnswers.Enqueue([false]);
        var gateHeldDuringMessage = true;
        _app.Dialogs.OnMessage = _ => gateHeldDuringMessage = _app.Services.Gate.IsBusy;
        await archive.RestoreToFolderCommand.ExecuteAsync(null);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(destination, "keep.txt")));
        Assert.False(gateHeldDuringMessage); // the result dialog does not hold up backups or quitting
        _app.Dialogs.OnMessage = null;

        _app.Dialogs.Confirmations.Enqueue(true);
        await archive.DeleteSnapshotCommand.ExecuteAsync(null);
        Assert.Empty(archive.Snapshots);
        Assert.True(archive.IsEmpty);
    }

    [Fact]
    public void Settings_save_the_history_limit()
    {
        var settings = new SettingsViewModel(_app.Shell) { HistoryLimit = 25 };

        Assert.Equal(25, _app.Services.Config.Settings.HistoryLimit);
        Assert.Equal(25, new AppStore(_app.AppDirectory).Load().Config!.Settings.HistoryLimit);
        Assert.Equal(25, _app.Services.Runner.HistoryLimit);
        Assert.Contains(settings.Licenses, l => l.Component == "Avalonia UI" && l.License == "MIT");
        Assert.Contains(settings.Licenses, l => l.License.Contains("Open Font", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_export_and_import_backups()
    {
        var (job, _) = _app.SavedJob();
        var file = Path.Combine(_app.BasePath, "backups.json");
        var settings = new SettingsViewModel(_app.Shell);

        _app.Dialogs.OptionAnswers.Enqueue([true]);
        _app.Dialogs.SaveFiles.Enqueue(file);
        await settings.ExportCommand.ExecuteAsync(null);
        Assert.True(File.Exists(file));

        _app.Services.Save(new AppConfig());
        _app.Dialogs.OpenFiles.Enqueue(file);
        await settings.ImportCommand.ExecuteAsync(null);

        Assert.Equal(job.Id, Assert.Single(_app.Services.Config.Jobs).Id);
        Assert.Contains(_app.Dialogs.Messages, m => m.StartsWith("Import finished", StringComparison.Ordinal) && m.Contains("Added: Photos", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clean_up_everything_removes_the_data_and_closes_the_app()
    {
        var (_, target) = _app.SavedJob();
        var exited = false;
        _app.Shell.ExitRequested += (_, _) => exited = true;
        _app.Dialogs.OptionAnswers.Enqueue([true, false]);

        await new SettingsViewModel(_app.Shell).CleanUpEverythingCommand.ExecuteAsync(null);

        Assert.True(exited);
        Assert.False(File.Exists(Path.Combine(_app.AppDirectory, AppStore.ConfigFileName)));
        Assert.False(File.Exists(Path.Combine(target.LastKnownPath, ".amberkeep", "target.json")));
    }

    private async Task<JobConfig> BackUpOnceAsync(string name, string file)
    {
        var source = new Infrastructure.TreeBuilder(_app.Create($"src-{name}")).File(file, name);
        var job = new JobConfig { Name = name, SourcePath = source.Root, RequireApproval = false };
        var target = _app.Services.Registry.AddTarget(_app.Services.Config, job, Path.Combine(_app.VolumeA, name), "Disk");
        job = job.WithTarget(target);
        _app.Services.Save(_app.Services.Config.WithJob(job));
        await RunAsync(job, target);
        return job;
    }

    private async Task RunAsync(JobConfig job, TargetConfig target)
    {
        var prepared = await _app.Services.Runner.AnalyzeAsync(job, target, cancellationToken: Ct);
        var record = await _app.Services.Runner.ExecuteAsync(
            prepared, prepared.Diff!.Items.Where(i => i.DefaultSelected).ToHashSet(), approved: true, cancellationToken: Ct);
        Assert.Equal(ExecutionOutcome.Completed, record.Outcome);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++)
        {
            await Task.Delay(20, Ct);
        }

        Assert.True(condition());
    }
}
