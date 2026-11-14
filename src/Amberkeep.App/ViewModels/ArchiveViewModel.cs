using System.Collections.ObjectModel;
using Amberkeep.App.Services;
using Amberkeep.Core.Paths;
using Amberkeep.Storage.Archive;
using Amberkeep.Storage.Config;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberkeep.App.ViewModels;

public sealed partial class SnapshotRowViewModel(SnapshotInfo snapshot) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private SnapshotInfo _snapshot = snapshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _isMeasured;

    public string When { get; } = snapshot.Time.ToLocalTime().ToString("d MMM yyyy, HH:mm:ss", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    public string Summary => IsMeasured
        ? $"{Format.Count(Snapshot.FileCount, "file", "files")} · {Format.Bytes(Snapshot.Bytes)}"
        : "Counting files…";
}

public sealed partial class ArchivedFileRowViewModel(ArchivedFile file, ArchiveViewModel owner) : ObservableObject
{
    public ArchivedFile File { get; } = file;

    public string Path => File.RelativePath;

    public string SizeText { get; } = Format.Bytes(file.Length);

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => owner.UpdateSelection();
}

/// <summary>Archive of one target: snapshots, their files and restoring them (SPEC 6.6).</summary>
public sealed partial class ArchiveViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly ArchiveBrowser _browser;
    private readonly JobConfig _job;
    private readonly string _targetPath;
    private List<ArchivedFileRowViewModel> _allFiles = [];
    private CancellationTokenSource? _restoreCancellation;
    private bool _bulkUpdate;

    public ArchiveViewModel(IShell shell, JobConfig job, TargetConfig target, string targetPath)
    {
        _shell = shell;
        _job = job;
        _targetPath = targetPath;
        _browser = new ArchiveBrowser(shell.Services.FileSystem);
        Title = $"{job.Name} → {target.Name}";
    }

    public string Title { get; }

    public string RetentionText => _job.Archive.RetentionDays is { } days
        ? $"Snapshots older than {days} days are deleted automatically."
        : "Snapshots are kept forever.";

    public ObservableCollection<SnapshotRowViewModel> Snapshots { get; } = [];

    [ObservableProperty]
    private IReadOnlyList<ArchivedFileRowViewModel> _files = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreToSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreToFolderCommand))]
    private SnapshotRowViewModel? _selectedSnapshot;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreToSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreToFolderCommand))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreToSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreToFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSnapshotCommand))]
    private bool _isRestoring;

    [ObservableProperty]
    private string _restoreStatus = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    public string SelectionText => Format.Count(SelectedCount, "file selected", "files selected");

    partial void OnSelectedSnapshotChanged(SnapshotRowViewModel? value) => _ = LoadFilesAsync(value);

    partial void OnSearchTextChanged(string value) => ApplySearch();

    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(SelectionText));

    public async Task LoadAsync()
    {
        // The list appears at once; file counts and sizes follow, one snapshot at a time.
        var snapshots = await Task.Run(() => _browser.ListSnapshots(_targetPath, withTotals: false));
        Snapshots.Clear();
        foreach (var snapshot in snapshots)
        {
            Snapshots.Add(new SnapshotRowViewModel(snapshot));
        }

        IsEmpty = Snapshots.Count == 0;
        SelectedSnapshot = Snapshots.FirstOrDefault();

        foreach (var row in Snapshots.ToList())
        {
            try
            {
                row.Snapshot = await Task.Run(() => _browser.Measure(row.Snapshot));
                row.IsMeasured = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave the row without totals; its files are still listed when selected.
            }
        }
    }

    internal void UpdateSelection()
    {
        if (!_bulkUpdate)
        {
            SelectedCount = _allFiles.Count(f => f.IsSelected);
        }
    }

    [RelayCommand]
    private void Back() => _shell.ShowDashboard();

    [RelayCommand]
    private void SelectAll() => SetSelected(Files, true);

    [RelayCommand]
    private void SelectNone() => SetSelected(_allFiles, false);

    private bool CanRestore() => SelectedSnapshot is not null && SelectedCount > 0 && !IsRestoring;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreToSourceAsync() => RestoreAsync(_job.SourcePath);

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreToFolderAsync()
    {
        if (await _shell.Dialogs.PickFolderAsync("Restore files to") is { } folder)
        {
            await RestoreAsync(folder);
        }
    }

    [RelayCommand]
    private Task CancelRestoreAsync() => _restoreCancellation?.CancelAsync() ?? Task.CompletedTask;

    private bool CanDelete() => SelectedSnapshot is not null && !IsRestoring;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteSnapshotAsync()
    {
        if (SelectedSnapshot is not { } row
            || !await _shell.Dialogs.ConfirmAsync(
                "Delete this snapshot?",
                $"All {Format.Count(row.Snapshot.FileCount, "file", "files")} archived on {row.When} are deleted permanently.",
                "Delete",
                destructive: true))
        {
            return;
        }

        string? failure = null;
        if (!await WithGateAsync(() =>
            {
                try
                {
                    _browser.DeleteSnapshot(row.Snapshot);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    failure = ex.Message;
                }
            }))
        {
            return;
        }

        if (failure is not null)
        {
            await _shell.Dialogs.ShowMessageAsync("The snapshot could not be deleted", failure);
        }

        await LoadAsync();
    }

    [RelayCommand]
    private async Task CleanUpOldAsync()
    {
        if (_job.Archive.RetentionDays is not { } days)
        {
            await _shell.Dialogs.ShowMessageAsync("Nothing to clean up", "This backup keeps its archive forever. Change it in the backup settings.");
            return;
        }

        string message;
        string title;
        IReadOnlyList<string>? deleted = null;
        string? failure = null;
        if (!await WithGateAsync(() =>
            {
                try
                {
                    deleted = _browser.PurgeOld(_targetPath, TimeSpan.FromDays(days), _shell.Services.Time.GetUtcNow());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure = ex.Message;
                }
            }))
        {
            return;
        }

        if (failure is not null)
        {
            title = "The archive could not be cleaned up";
            message = failure;
        }
        else
        {
            title = "Archive cleaned up";
            message = deleted is not { Count: > 0 } ? $"No snapshot is older than {days} days." : $"{Format.Count(deleted.Count, "snapshot", "snapshots")} deleted.";
        }

        await _shell.Dialogs.ShowMessageAsync(title, message);

        await LoadAsync();
    }

    // Deleting archive snapshots must not run beside a backup that writes into the same archive. The work runs off
    // the UI thread: deleting a large snapshot on a slow disk takes a while.
    private async Task<bool> WithGateAsync(Action action)
    {
        if (!_shell.Services.Gate.TryEnter())
        {
            await JobFlow.ShowBusyAsync(_shell);
            return false;
        }

        try
        {
            await Task.Run(action);
        }
        finally
        {
            _shell.Services.Gate.Exit();
        }

        return true;
    }

    private async Task RestoreAsync(string destination)
    {
        if (SelectedSnapshot is not { } row)
        {
            return;
        }

        var paths = _allFiles.Where(f => f.IsSelected).Select(f => f.Path).ToList();
        var intoTarget = Core.Paths.PathOverlap.Check(_targetPath, destination, Core.Paths.CaseSensitivity.Auto.IsCaseSensitive())
            is Core.Paths.PathRelation.Same or Core.Paths.PathRelation.TargetInsideSource;
        var message = $"{Format.Count(paths.Count, "file is", "files are")} copied to {destination}, keeping their folders. The archive stays as it is.";
        if (intoTarget)
        {
            message += " Note: this folder is the backup target – the next run removes files that are not in the source.";
        }

        var answers = await _shell.Dialogs.ConfirmWithOptionsAsync(
            "Restore files?", message, "Restore", [new DialogOption("Overwrite files that already exist")]);
        if (answers is null)
        {
            return;
        }

        // Not while a backup runs on this target; Quit stops the restore like a backup.
        if (!_shell.Services.Gate.TryEnter())
        {
            await JobFlow.ShowBusyAsync(_shell);
            return;
        }

        IsRestoring = true;
        _restoreCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shell.Services.Gate.Token);
        var progress = new Progress<RestoreProgress>(p => RestoreStatus = $"Restoring {p.Done:N0} of {p.Total:N0}…  {p.CurrentPath}");
        string resultTitle;
        string resultMessage;
        try
        {
            var token = _restoreCancellation.Token;
            var result = await Task.Run(() => _browser.RestoreAsync(row.Snapshot, paths, destination, answers[0], progress, token));
            var lines = new List<string> { $"{Format.Count(result.Restored, "file", "files")} restored." };
            if (result.SkippedExisting > 0)
            {
                lines.Add($"{Format.Count(result.SkippedExisting, "file was", "files were")} skipped because it already exists.");
            }

            lines.AddRange(result.Failures.Select(f => $"{f.RelativePath}: {f.Message}"));
            resultTitle = result.Cancelled ? "Restore stopped" : "Restore finished";
            resultMessage = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            resultTitle = "The files could not be restored";
            resultMessage = ex.Message;
        }
        finally
        {
            // Released before the result is shown, so an open dialog never holds up backups or quitting.
            IsRestoring = false;
            RestoreStatus = string.Empty;
            _restoreCancellation.Dispose();
            _restoreCancellation = null;
            _shell.Services.Gate.Exit();
        }

        await _shell.Dialogs.ShowMessageAsync(resultTitle, resultMessage);
    }

    private async Task LoadFilesAsync(SnapshotRowViewModel? row)
    {
        if (row is null)
        {
            _allFiles = [];
            ApplySearch();
            UpdateSelection();
            return;
        }

        var files = await Task.Run(() => _browser.ListFiles(row.Snapshot));
        if (SelectedSnapshot != row)
        {
            return;
        }

        _allFiles = files.Select(f => new ArchivedFileRowViewModel(f, this)).ToList();
        ApplySearch();
        UpdateSelection();
    }

    private void ApplySearch()
    {
        var search = SearchText.Trim();
        Files = search.Length == 0
            ? _allFiles
            : _allFiles.Where(f => f.Path.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void SetSelected(IEnumerable<ArchivedFileRowViewModel> files, bool value)
    {
        _bulkUpdate = true;
        foreach (var file in files)
        {
            file.IsSelected = value;
        }

        _bulkUpdate = false;
        UpdateSelection();
    }
}
