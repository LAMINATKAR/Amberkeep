using Amberkeep.Core.Diff;
using Amberkeep.Core.Exclusions;
using Amberkeep.Core.Execution;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberkeep.App.ViewModels;

/// <summary>
/// One-time run (SPEC 4.7): mirror a folder without a saved job – no marker, no settings, no history.
/// The diff is always shown, because the target is not verified by a marker.
/// </summary>
public sealed partial class OneTimeRunViewModel : ObservableObject
{
    private readonly IShell _shell;

    public OneTimeRunViewModel(IShell shell)
    {
        _shell = shell;
        _exclusionsText = string.Join(Environment.NewLine, DefaultExclusions.Patterns);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private string _targetPath = string.Empty;

    [ObservableProperty]
    private bool _compareContent;

    [ObservableProperty]
    private bool _archiveEnabled;

    [ObservableProperty]
    private string _exclusionsText;

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        if (await _shell.Dialogs.PickFolderAsync("Folder to copy from") is { } folder)
        {
            SourcePath = folder;
        }
    }

    [RelayCommand]
    private async Task BrowseTargetAsync()
    {
        if (await _shell.Dialogs.PickFolderAsync("Folder to mirror into") is { } folder)
        {
            TargetPath = folder;
        }
    }

    [RelayCommand]
    private void Cancel() => _shell.ShowDashboard();

    private bool CanAnalyze() =>
        Path.IsPathFullyQualified(SourcePath.Trim()) && Path.IsPathFullyQualified(TargetPath.Trim());

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        var source = SourcePath.Trim();
        var target = TargetPath.Trim();
        var options = new DiffOptions
        {
            CompareMode = CompareContent ? CompareMode.Content : CompareMode.SizeAndTime,
            Exclusions = ExclusionsText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        };

        var title = "One-time run";
        var busy = new BusyViewModel("Analyzing", $"{source} → {target}");
        _shell.Show(busy);

        DiffResult diff;
        try
        {
            diff = await new DiffEngine(_shell.Services.FileSystem)
                .AnalyzeAsync(new DiffRequest(source, target, options), busy.Progress, busy.Token);
        }
        catch (OperationCanceledException)
        {
            _shell.Show(this);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await _shell.Dialogs.ShowMessageAsync("Analysis failed", ex.Message);
            _shell.Show(this);
            return;
        }

        // Never purge old archive snapshots: the target may also be a saved backup with its own retention.
        var execution = new ExecutionOptions { ArchiveEnabled = ArchiveEnabled, ArchiveRetention = null, Approved = true };
        _shell.Show(new DiffViewModel(
            _shell,
            diff,
            title,
            ArchiveEnabled,
            selection =>
            {
                var run = new RunViewModel(
                    _shell,
                    title,
                    diff.SourcePath,
                    diff.TargetPath,
                    async (progress, ct) => RunOutcome.From(
                        await new MirrorExecutor(_shell.Services.FileSystem, _shell.Services.Time)
                            .ExecuteAsync(diff, selection, execution, progress, ct)),
                    canSaveReport: true);
                _shell.Show(run);
                return run.StartAsync();
            },
            () => _shell.Show(this)));
    }
}
