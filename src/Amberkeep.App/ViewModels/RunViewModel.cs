using System.Collections.ObjectModel;
using System.Diagnostics;
using Amberkeep.App.Services;
using Amberkeep.Core.Execution;
using Amberkeep.Storage.History;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberkeep.App.ViewModels;

/// <summary>What a finished run reports, from a job run (history record) or a one-time run.</summary>
public sealed record RunOutcome(
    ExecutionOutcome Outcome,
    string? RefusedReason,
    int CopiedFiles,
    int ReplacedFiles,
    int RemovedFiles,
    int RemovedDirectories,
    int CreatedDirectories,
    int RenamedEntries,
    int UpdatedTimestamps,
    int ArchivedFiles,
    long CopiedBytes,
    IReadOnlyList<ExecutionFailure> Failures,
    IReadOnlyList<string> Notes,
    string? ArchiveFolder,
    TimeSpan Duration)
{
    public static RunOutcome From(ExecutionResult r) => new(
        r.Outcome, r.RefusedReason, r.CopiedFiles, r.ReplacedFiles, r.RemovedFiles, r.RemovedDirectories, r.CreatedDirectories,
        r.RenamedEntries, r.UpdatedTimestamps, r.ArchivedFiles, r.CopiedBytes, r.Failures, r.Notes, r.ArchiveFolder, r.Duration);

    public static RunOutcome From(RunRecord r) => new(
        r.Outcome, r.RefusedReason, r.CopiedFiles, r.ReplacedFiles, r.RemovedFiles, r.RemovedDirectories, r.CreatedDirectories,
        r.RenamedEntries, r.UpdatedTimestamps, r.ArchivedFiles, r.CopiedBytes, r.Failures, r.Notes, r.ArchiveFolder,
        r.FinishedUtc - r.StartedUtc);
}

public sealed record StatViewModel(string Label, string Value);

/// <summary>A running backup with progress, then its result.</summary>
public sealed partial class RunViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly Func<IProgress<ExecutionProgress>, CancellationToken, Task<RunOutcome>> _execute;
    private readonly Action? _onDone;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly Queue<(TimeSpan Time, long Bytes)> _samples = new();

    public RunViewModel(
        IShell shell,
        string title,
        string sourcePath,
        string targetPath,
        Func<IProgress<ExecutionProgress>, CancellationToken, Task<RunOutcome>> execute,
        Action? onDone = null,
        bool canSaveReport = false)
    {
        _shell = shell;
        Title = title;
        SourcePath = sourcePath;
        TargetPath = targetPath;
        _execute = execute;
        _onDone = onDone;
        CanSaveReport = canSaveReport;
    }

    public string Title { get; }

    public string SourcePath { get; }

    public string TargetPath { get; }

    public bool CanSaveReport { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning = true;

    public bool IsFinished => !IsRunning;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private string _operationText = "Preparing…";

    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    private string _countText = string.Empty;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private RunOutcome? _outcome;

    [ObservableProperty]
    private string _outcomeText = string.Empty;

    [ObservableProperty]
    private BannerKind _outcomeKind;

    public ObservableCollection<StatViewModel> Stats { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    public bool HasArchive => Outcome?.ArchiveFolder is not null;

    /// <summary>Starts the run; returns when it has finished.</summary>
    public async Task StartAsync()
    {
        _stopwatch.Start();
        var progress = new Progress<ExecutionProgress>(OnProgress);
        RunOutcome outcome;
        try
        {
            outcome = await _execute(progress, _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = new RunOutcome(
                ExecutionOutcome.Cancelled, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], null, _stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the page shows a result and can be left; it never stays on "Backing up".
            if (ex is not (IOException or UnauthorizedAccessException or InvalidOperationException))
            {
                Services.ErrorLog.Write(ex, $"Backup {Title}");
            }

            outcome = new RunOutcome(
                ExecutionOutcome.Refused, ex.Message, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], null, _stopwatch.Elapsed);
        }

        ShowOutcome(outcome);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task CancelAsync()
    {
        if (await _shell.Dialogs.ConfirmAsync(
                "Stop the backup?",
                "Files copied so far stay on the target. Nothing is left half-written, and the next run continues where this one stopped.",
                "Stop",
                destructive: true))
        {
            await _cancellation.CancelAsync();
        }
    }

    [RelayCommand]
    private void Done()
    {
        if (_onDone is not null)
        {
            _onDone();
        }
        else
        {
            _shell.ShowDashboard();
        }
    }

    [RelayCommand]
    private void OpenArchive()
    {
        if (Outcome?.ArchiveFolder is { } folder)
        {
            _shell.Dialogs.OpenFolder(folder);
        }
    }

    [RelayCommand]
    private async Task SaveReportAsync()
    {
        if (Outcome is null)
        {
            return;
        }

        var path = await _shell.Dialogs.PickSaveFileAsync("Save report", $"amberkeep-report-{DateTime.Now:yyyyMMdd-HHmmss}.html");
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, ReportBuilder.Build(CreateReport(), path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _shell.Dialogs.ShowMessageAsync("Report not saved", ex.Message);
        }
    }

    internal RunReport CreateReport() => new(
        Title,
        SourcePath,
        TargetPath,
        null,
        Format.Outcome(Outcome!.Outcome),
        Stats.ToList(),
        Problems.ToList(),
        Notes.ToList(),
        [],
        ChangesTruncated: false);

    private void OnProgress(ExecutionProgress p)
    {
        OperationText = p.Operation switch
        {
            ExecutionOperation.Remove => "Removing",
            ExecutionOperation.Rename => "Renaming",
            ExecutionOperation.CreateDirectory => "Creating folders",
            ExecutionOperation.Copy => "Copying",
            ExecutionOperation.UpdateTimestamp => "Updating times",
            ExecutionOperation.CleanArchive => "Cleaning the archive",
            _ => "Preparing",
        };
        CurrentPath = p.RelativePath;
        CountText = $"{p.CompletedOperations:N0} of {p.TotalOperations:N0} changes";

        if (p.TotalBytes > 0)
        {
            IsIndeterminate = false;
            Percent = 100.0 * p.CopiedBytes / p.TotalBytes;
        }
        else if (p.TotalOperations > 0)
        {
            IsIndeterminate = false;
            Percent = 100.0 * p.CompletedOperations / p.TotalOperations;
        }

        UpdateSpeed(p);
    }

    private void UpdateSpeed(ExecutionProgress p)
    {
        var now = _stopwatch.Elapsed;
        _samples.Enqueue((now, p.CopiedBytes));
        while (_samples.Count > 2 && now - _samples.Peek().Time > TimeSpan.FromSeconds(10))
        {
            _samples.Dequeue();
        }

        var (oldTime, oldBytes) = _samples.Peek();
        var seconds = (now - oldTime).TotalSeconds;
        if (seconds < 1 || p.CopiedBytes <= oldBytes)
        {
            return;
        }

        var bytesPerSecond = (p.CopiedBytes - oldBytes) / seconds;
        var remaining = TimeSpan.FromSeconds((p.TotalBytes - p.CopiedBytes) / bytesPerSecond);
        SpeedText = $"{Format.Bytes((long)bytesPerSecond)}/s  ·  about {Format.Duration(remaining)} left";
    }

    private void ShowOutcome(RunOutcome o)
    {
        Outcome = o;
        IsRunning = false;
        Percent = 100;
        IsIndeterminate = false;
        OutcomeText = Format.Outcome(o.Outcome);
        OutcomeKind = o.Outcome switch
        {
            ExecutionOutcome.Completed => BannerKind.Info,
            ExecutionOutcome.CompletedWithErrors or ExecutionOutcome.Cancelled => BannerKind.Warning,
            _ => BannerKind.Error,
        };

        foreach (var stat in RunStats.From(o))
        {
            Stats.Add(stat);
        }

        foreach (var problem in RunStats.Problems(o))
        {
            Problems.Add(problem);
        }

        foreach (var note in o.Notes)
        {
            Notes.Add(note);
        }

        OnPropertyChanged(nameof(HasArchive));
    }
}
