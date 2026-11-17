using System.Collections.ObjectModel;
using Amberkeep.App.Services;
using Amberkeep.Core.Execution;
using Amberkeep.Storage.History;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberkeep.App.ViewModels;

public sealed record JobFilter(Guid? JobId, string Label);

public sealed class HistoryRowViewModel(RunRecord record, DateTimeOffset now, bool isDeleted = false)
{
    private readonly RunOutcome _outcome = RunOutcome.From(record);

    public RunRecord Record { get; } = record;

    /// <summary>The backup of this run has been deleted (its runs are kept unless the user deleted them too).</summary>
    public bool IsDeleted { get; } = isDeleted;

    public string When { get; } = Format.When(record.StartedUtc, now);

    public string Title { get; } = $"{record.JobName} → {record.TargetName}";

    public string Summary => RunStats.Summary(_outcome);

    public string OutcomeText { get; } = Format.Outcome(record.Outcome);

    public BannerKind OutcomeKind { get; } = record.Outcome switch
    {
        ExecutionOutcome.Completed => BannerKind.Info,
        ExecutionOutcome.CompletedWithErrors or ExecutionOutcome.Cancelled => BannerKind.Warning,
        _ => BannerKind.Error,
    };

    public string Duration { get; } = Format.Duration(record.FinishedUtc - record.StartedUtc);
}

/// <summary>Past runs with their details and reports (SPEC 6.5).</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IShell _shell;
    private IReadOnlyList<RunRecord> _records = [];

    public HistoryViewModel(IShell shell)
    {
        _shell = shell;
        Filters = [new JobFilter(null, "All backups"), .. shell.Services.Config.Jobs.Select(j => new JobFilter(j.Id, j.Name))];
        _selectedFilter = Filters[0];
    }

    public ObservableCollection<JobFilter> Filters { get; }

    public ObservableCollection<HistoryRowViewModel> Runs { get; } = [];

    public ObservableCollection<StatViewModel> Stats { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty]
    private IReadOnlyList<RecordedChange> _changes = [];

    [ObservableProperty]
    private JobFilter _selectedFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenArchiveCommand))]
    private HistoryRowViewModel? _selectedRun;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string? _changesNote;

    public bool HasSelection => SelectedRun is not null;

    partial void OnSelectedFilterChanged(JobFilter value) => ApplyFilter();

    partial void OnSelectedRunChanged(HistoryRowViewModel? value) => _ = LoadDetailAsync(value);

    /// <summary>Reads the history (off the UI thread) and selects the newest run.</summary>
    public async Task LoadAsync()
    {
        _records = await Task.Run(_shell.Services.History.List);

        // Backups that were deleted but still have runs in the history can be filtered too.
        foreach (var deleted in _records.Where(r => Filters.All(f => f.JobId != r.JobId)).GroupBy(r => r.JobId))
        {
            Filters.Add(new JobFilter(deleted.Key, $"{deleted.First().JobName} (deleted)"));
        }

        ApplyFilter();
    }

    [RelayCommand]
    private void Back() => _shell.ShowDashboard();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ExportReportAsync()
    {
        if (SelectedRun is not { } run)
        {
            return;
        }

        var path = await _shell.Dialogs.PickSaveFileAsync(
            "Export report", SafeFileName($"amberkeep-{run.Record.JobName}-{run.Record.StartedUtc.ToLocalTime():yyyyMMdd-HHmmss}.html"));
        if (path is null)
        {
            return;
        }

        try
        {
            var changes = await Task.Run(() => _shell.Services.History.ReadChanges(run.Record));
            await File.WriteAllTextAsync(path, ReportBuilder.Build(RunReport.From(run.Record, changes), path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _shell.Dialogs.ShowMessageAsync("Report not saved", ex.Message);
        }
    }

    private bool CanOpenArchive() => SelectedRun?.Record.ArchiveFolder is { } folder && Directory.Exists(folder);

    [RelayCommand(CanExecute = nameof(CanOpenArchive))]
    private void OpenArchive()
    {
        if (SelectedRun?.Record.ArchiveFolder is { } folder)
        {
            _shell.Dialogs.OpenFolder(folder);
        }
    }

    private void ApplyFilter()
    {
        var now = _shell.Services.Time.GetUtcNow();
        var config = _shell.Services.Config;
        Runs.Clear();
        foreach (var record in _records.Where(r => SelectedFilter.JobId is null || r.JobId == SelectedFilter.JobId))
        {
            Runs.Add(new HistoryRowViewModel(record, now, config.FindJob(record.JobId) is null));
        }

        IsEmpty = Runs.Count == 0;
        SelectedRun = Runs.FirstOrDefault();
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 || c is ':' or '/' or '\\' ? '-' : c).ToArray());
    }

    private async Task LoadDetailAsync(HistoryRowViewModel? row)
    {
        Stats.Clear();
        Problems.Clear();
        Notes.Clear();
        Changes = [];
        ChangesNote = null;
        if (row is null)
        {
            return;
        }

        var outcome = RunOutcome.From(row.Record);
        foreach (var stat in RunStats.From(outcome))
        {
            Stats.Add(stat);
        }

        foreach (var problem in RunStats.Problems(outcome))
        {
            Problems.Add(problem);
        }

        foreach (var note in row.Record.Notes)
        {
            Notes.Add(note);
        }

        var changes = await Task.Run(() => _shell.Services.History.ReadChanges(row.Record));
        if (SelectedRun != row)
        {
            return;
        }

        Changes = changes;

        ChangesNote = row.Record.ChangesTruncated
            ? $"Only the first {changes.Count:N0} changes are listed."
            : changes.Count == 0 ? "No changes were selected in this run." : null;
    }
}
