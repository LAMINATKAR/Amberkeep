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
            ProtectedFolders = [_shell.Services.Store.BaseDirectory],
        };

        var title = "One-time run";
        if (!_shell.Services.Gate.TryEnter())
        {
            await JobFlow.ShowBusyAsync(_shell);
            return;
        }

        var busy = new BusyViewModel("Analyzing", $"{source} → {target}");
        _shell.Show(busy);

        DiffResult? diff = null;
        string? failure = null;
        try
        {
            _shell.Services.Activity.Analyzing(title);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(busy.Token, _shell.Services.Gate.Token);
            // Off the UI thread: the path checks can wait for an unreachable network folder.
            var token = cancellation.Token;
            var engine = new DiffEngine(_shell.Services.FileSystem);
            diff = await Task.Run(() => engine.AnalyzeAsync(new DiffRequest(source, target, options), busy.Progress, token));
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user, or the app is quitting.
        }
        catch (Exception ex)
        {
            if (ex is not (IOException or UnauthorizedAccessException or ArgumentException))
            {
                Services.ErrorLog.Write(ex, title);
            }

            failure = ex.Message;
        }
        finally
        {
            // Released before any dialog, so an automatic run is not held up while the message is open.
            _shell.Services.Activity.Idle();
            _shell.Services.Gate.Exit();
        }

        if (failure is not null)
        {
            await _shell.Dialogs.ShowMessageAsync("Analysis failed", failure);
        }

        if (diff is null)
        {
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
                    async (progress, ct) => await JobFlow.GuardedAsync(
                            _shell.Services,
                            title,
                            progress,
                            async (tracked, gateToken) =>
                            {
                                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, gateToken);
                                return RunOutcome.From(
                                    await new MirrorExecutor(_shell.Services.FileSystem, _shell.Services.Time)
                                        .ExecuteAsync(diff, selection, execution, tracked, cancellation.Token));
                            })
                        ?? throw new InvalidOperationException("Another backup is running, possibly in the background. Try again when it has finished."),
                    canSaveReport: true);
                _shell.Show(run);
                return run.StartAsync();
            },
            () => _shell.Show(this)));
    }
}
