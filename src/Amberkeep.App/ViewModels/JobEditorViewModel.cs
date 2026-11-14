using System.Collections.ObjectModel;
using Amberkeep.App.Services;
using Amberkeep.Core.Diff;
using Amberkeep.Core.Paths;
using Amberkeep.Core.Safety;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.Targets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberkeep.App.ViewModels;

/// <summary>A target in the editor. New targets get their marker only when the job is saved.</summary>
public sealed partial class EditorTargetViewModel(TargetConfig target, bool isNew, bool takeOver) : ObservableObject
{
    public TargetConfig Target { get; } = target;

    public bool IsNew { get; } = isNew;

    /// <summary>The folder has a marker of another backup and the user agreed to take it over.</summary>
    public bool TakeOver { get; } = takeOver;

    public string Name => Target.Name;

    public string Path => Target.LastKnownPath;

    public string Note => IsNew ? "added – saved with the backup" : string.Empty;
}

public sealed record CaseChoice(CaseSensitivity Value, string Label);

public sealed record ScheduleChoice(ScheduleKind Value, string Label);

/// <summary>One weekday button of a weekly schedule.</summary>
public sealed partial class DayToggle(DayOfWeek value, bool isSelected, Action changed) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = isSelected;

    public DayOfWeek Value { get; } = value;

    public string Label { get; } = value.ToString()[..3];

    partial void OnIsSelectedChanged(bool value) => changed();
}

public sealed record MonthDayChoice(int Value, string Label);

/// <summary>Creates or edits a backup job (SPEC 6.2).</summary>
public sealed partial class JobEditorViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly JobConfig _original;
    private readonly bool _isNew;
    private readonly List<(TargetConfig Target, bool RemoveMarker, bool RemoveArchive)> _removed = [];
    private bool _touched;

    public JobEditorViewModel(IShell shell, JobConfig? job)
    {
        _shell = shell;
        _isNew = job is null;
        _original = job ?? new JobConfig();

        _name = _original.Name;
        _sourcePath = _original.SourcePath;
        _compareContent = _original.CompareMode == CompareMode.Content;
        _caseSensitivity = CaseChoices.First(c => c.Value == _original.CaseSensitivity);
        _ignoreOneHourShift = _original.IgnoreOneHourShift;
        _exclusionsText = string.Join(Environment.NewLine, _original.Exclusions);
        _requireApproval = _original.RequireApproval;
        _verifyCopies = _original.VerifyCopies;
        _guardEnabled = _original.MassRemovalGuard.Enabled;
        _guardPercent = _original.MassRemovalGuard.Percent;
        _guardCount = _original.MassRemovalGuard.Count;
        _archiveEnabled = _original.Archive.Enabled;
        _keepArchiveForever = _original.Archive.RetentionDays is null;
        _retentionDays = _original.Archive.RetentionDays ?? 30;
        var trigger = _original.Trigger;
        _runOnConnect = trigger.OnTargetConnected;
        _scheduleKind = ScheduleChoices.First(c => c.Value == trigger.Schedule.Kind);
        _scheduleTime = trigger.Schedule.Time.ToTimeSpan();
        DayToggles = [.. WeekDays.Select(d => new DayToggle(d, trigger.Schedule.DaysOfWeek.Contains(d), Validate))];
        _scheduleDayOfMonth = MonthDayChoices.FirstOrDefault(d => d.Value == trigger.Schedule.DayOfMonth) ?? MonthDayChoices[0];
        _intervalHours = trigger.Schedule.IntervalHours;

        Targets = new ObservableCollection<EditorTargetViewModel>(
            _original.Targets.Select(t => new EditorTargetViewModel(t, isNew: false, takeOver: false)));
        Validate();
    }

    public string Title => _isNew ? "New backup" : $"Edit '{_original.Name}'";

    public IReadOnlyList<CaseChoice> CaseChoices { get; } =
    [
        new(Core.Paths.CaseSensitivity.Auto, "Automatic (by operating system)"),
        new(Core.Paths.CaseSensitivity.Insensitive, "Ignore letter case"),
        new(Core.Paths.CaseSensitivity.Sensitive, "Letter case matters"),
    ];

    public ObservableCollection<EditorTargetViewModel> Targets { get; }

    public ObservableCollection<string> Errors { get; } = [];

    public bool HasErrors => Errors.Count > 0;

    public bool HasNoTargets => Targets.Count == 0;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _sourcePath;

    [ObservableProperty]
    private bool _compareContent;

    [ObservableProperty]
    private CaseChoice _caseSensitivity;

    [ObservableProperty]
    private bool _ignoreOneHourShift;

    [ObservableProperty]
    private string _exclusionsText;

    [ObservableProperty]
    private bool _requireApproval;

    [ObservableProperty]
    private bool _verifyCopies;

    [ObservableProperty]
    private bool _guardEnabled;

    [ObservableProperty]
    private decimal? _guardPercent;

    [ObservableProperty]
    private decimal? _guardCount;

    [ObservableProperty]
    private bool _archiveEnabled;

    [ObservableProperty]
    private bool _keepArchiveForever;

    [ObservableProperty]
    private decimal? _retentionDays;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _canSave;

    partial void OnNameChanged(string value) => Touch();

    partial void OnSourcePathChanged(string value) => Touch();

    partial void OnGuardPercentChanged(decimal? value) => Validate();

    partial void OnGuardCountChanged(decimal? value) => Validate();

    partial void OnRetentionDaysChanged(decimal? value) => Validate();

    partial void OnKeepArchiveForeverChanged(bool value) => Validate();

    /// <summary>The job as currently entered.</summary>
    public JobConfig BuildJob() => _original with
    {
        Name = Name.Trim(),
        SourcePath = SourcePath.Trim(),
        CompareMode = CompareContent ? CompareMode.Content : CompareMode.SizeAndTime,
        CaseSensitivity = CaseSensitivity.Value,
        IgnoreOneHourShift = IgnoreOneHourShift,
        Exclusions = ExclusionsText
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList(),
        RequireApproval = RequireApproval,
        VerifyCopies = VerifyCopies,
        MassRemovalGuard = new MassRemovalGuard(GuardEnabled, (int)(GuardPercent ?? 50), (int)(GuardCount ?? 1000)),
        Archive = new ArchiveSettings
        {
            Enabled = ArchiveEnabled,
            RetentionDays = KeepArchiveForever ? null : (int)(RetentionDays ?? 30),
        },
        Trigger = new TriggerConfig
        {
            OnTargetConnected = RunOnConnect,
            Schedule = BuildSchedule(),
        },
        Targets = Targets.Select(t => t.Target).ToList(),
    };

    /// <summary>
    /// The schedule as entered. When it differs from the saved one (or was just switched on) it counts from now,
    /// so earlier times are not treated as missed runs; an unchanged schedule keeps its start.
    /// </summary>
    private ScheduleConfig BuildSchedule()
    {
        var schedule = new ScheduleConfig
        {
            Kind = ScheduleKind.Value,
            Time = TimeOnly.FromTimeSpan(ScheduleTime ?? new TimeSpan(20, 0, 0)),
            DaysOfWeek = DayToggles.Where(d => d.IsSelected).Select(d => d.Value).ToList(),
            DayOfMonth = ScheduleDayOfMonth.Value,
            IntervalHours = (int)(IntervalHours ?? 6),
        };
        var saved = _original.Trigger.Schedule;
        return schedule.Equals(saved)
            ? schedule with { SinceUtc = saved.SinceUtc }
            : schedule with { SinceUtc = _shell.Services.Time.GetUtcNow() };
    }

    public IReadOnlyList<ScheduleChoice> ScheduleChoices { get; } =
    [
        new(Storage.Config.ScheduleKind.None, "No schedule"),
        new(Storage.Config.ScheduleKind.Daily, "Every day at"),
        new(Storage.Config.ScheduleKind.Weekly, "Every week on"),
        new(Storage.Config.ScheduleKind.Monthly, "Every month on the"),
        new(Storage.Config.ScheduleKind.Interval, "Every few hours"),
    ];

    private static readonly DayOfWeek[] WeekDays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>Monday to Sunday; the selected ones make up a weekly schedule.</summary>
    public IReadOnlyList<DayToggle> DayToggles { get; }

    public IReadOnlyList<MonthDayChoice> MonthDayChoices { get; } =
        [.. Enumerable.Range(1, 31).Select(d => new MonthDayChoice(d, d == 31 ? "31st / last day" : ScheduleCalculator.Ordinal(d)))];

    [ObservableProperty]
    private bool _runOnConnect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTime), nameof(ShowDays), nameof(ShowDayOfMonth), nameof(ShowAt), nameof(ShowInterval))]
    private ScheduleChoice _scheduleKind = null!;

    [ObservableProperty]
    private TimeSpan? _scheduleTime;

    [ObservableProperty]
    private MonthDayChoice _scheduleDayOfMonth;

    [ObservableProperty]
    private decimal? _intervalHours;

    public bool ShowTime => ScheduleKind.Value is Storage.Config.ScheduleKind.Daily or Storage.Config.ScheduleKind.Weekly or Storage.Config.ScheduleKind.Monthly;

    public bool ShowDays => ScheduleKind.Value == Storage.Config.ScheduleKind.Weekly;

    public bool ShowDayOfMonth => ScheduleKind.Value == Storage.Config.ScheduleKind.Monthly;

    /// <summary>"at" between the day(s) and the time; the daily label already ends with it.</summary>
    public bool ShowAt => ShowDays || ShowDayOfMonth;

    public bool ShowInterval => ScheduleKind.Value == Storage.Config.ScheduleKind.Interval;

    partial void OnIntervalHoursChanged(decimal? value) => Validate();

    partial void OnScheduleKindChanged(ScheduleChoice value) => Validate();

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        if (await _shell.Dialogs.PickFolderAsync("Folder to back up") is { } folder)
        {
            SourcePath = folder;
            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder));
            }
        }
    }

    [RelayCommand]
    private async Task AddTargetAsync()
    {
        if (!System.IO.Path.IsPathFullyQualified(SourcePath.Trim()))
        {
            await _shell.Dialogs.ShowMessageAsync("Choose the source first", "Pick the folder to back up before adding targets.");
            return;
        }

        var folder = await _shell.Dialogs.PickFolderAsync("Backup target folder");
        if (folder is null)
        {
            return;
        }

        var job = BuildJob();
        var config = _shell.Services.Config.WithJob(job);
        var programDirectory = _shell.Services.Store.BaseDirectory;
        if (await Task.Run(() => config.FindPathConflict(folder, pendingJob: job, programDirectory: programDirectory, onDisk: true)) is { } conflict)
        {
            await _shell.Dialogs.ShowMessageAsync("Cannot use this folder", conflict);
            return;
        }

        var takeOver = false;
        var marker = _shell.Services.Registry.ReadMarker(folder);
        if (marker.State == MarkerState.Present)
        {
            takeOver = await _shell.Dialogs.ConfirmAsync(
                "Folder already used",
                $"This folder is a backup target of '{marker.JobName}'. Use it for this backup instead? The other backup will no longer recognize it.",
                "Take over");
            if (!takeOver)
            {
                return;
            }
        }

        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder));
        var target = _shell.Services.Registry.WithLocation(
            new TargetConfig { Name = string.IsNullOrEmpty(name) ? folder : name, CreatedUtc = _shell.Services.Time.GetUtcNow() },
            System.IO.Path.GetFullPath(folder));
        Targets.Add(new EditorTargetViewModel(target, isNew: true, takeOver));
        OnPropertyChanged(nameof(HasNoTargets));
        Touch();
    }

    [RelayCommand]
    private async Task RemoveTargetAsync(EditorTargetViewModel target)
    {
        if (target.IsNew)
        {
            Targets.Remove(target);
        }
        else
        {
            var answers = await _shell.Dialogs.ConfirmWithOptionsAsync(
                $"Remove '{target.Name}'?",
                "The target is removed when you save. Backed-up files stay on the disk.",
                "Remove",
                [new DialogOption("Also remove the Amberkeep marker from the folder", true),
                 new DialogOption("Also delete archived old versions")],
                destructive: true);
            if (answers is null)
            {
                return;
            }

            _removed.Add((target.Target, answers[0], answers[1]));
            Targets.Remove(target);
        }

        OnPropertyChanged(nameof(HasNoTargets));
        Touch();
    }

    [RelayCommand]
    private void ResetExclusions() =>
        ExclusionsText = string.Join(Environment.NewLine, Core.Exclusions.DefaultExclusions.Patterns);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var services = _shell.Services;

        // 0. Where the folders really are (junctions, links, drive aliases): checked once here, not on every
        //    keystroke, and off the UI thread, because an unreachable network folder can take a while to answer.
        var draft = BuildJob();
        var onDisk = await Task.Run(() => services.Config.Validate(draft, services.Store.BaseDirectory, onDisk: true));
        if (onDisk.Count > 0)
        {
            await _shell.Dialogs.ShowMessageAsync("The backup could not be saved", string.Join(Environment.NewLine, onDisk));
            return;
        }

        // Not while a backup runs: saving writes markers and removes the files of removed targets. The gate is
        // released before any message is shown, so an open dialog never holds up automatic runs.
        if (!services.Gate.TryEnter())
        {
            await JobFlow.ShowBusyAsync(_shell);
            return;
        }

        SaveResult result;
        try
        {
            result = await SaveCoreAsync();
        }
        finally
        {
            services.Gate.Exit();
        }

        if (result.Message is { } message)
        {
            await _shell.Dialogs.ShowMessageAsync(result.Title!, message);
        }

        if (result.Saved)
        {
            _shell.ShowDashboard();
        }
    }

    private sealed record SaveResult(bool Saved, string? Title = null, string? Message = null);

    private async Task<SaveResult> SaveCoreAsync()
    {
        var services = _shell.Services;

        // 1. Markers of new targets. If one fails, the markers written so far are removed again
        //    and nothing has been saved yet.
        var job = BuildJob() with { Targets = Targets.Where(t => !t.IsNew).Select(t => t.Target).ToList() };
        var created = new List<(EditorTargetViewModel Row, TargetConfig Target)>();
        try
        {
            foreach (var added in Targets.Where(t => t.IsNew).ToList())
            {
                var pending = job;
                var target = await Task.Run(() => services.Registry.AddTarget(
                    services.Config.WithJob(pending), pending, added.Path, added.Name, replaceForeignMarker: added.TakeOver));
                created.Add((added, target));
                job = job.WithTarget(target);
            }

            // 2. One save with all changes.
            services.Save(services.Config.WithJob(job));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            foreach (var (_, target) in created)
            {
                services.Cleanup.CleanTargetFolder(job, target, removeMarker: true, removeArchive: false);
            }

            return new SaveResult(false, "The backup could not be saved", ex.Message);
        }

        foreach (var (row, target) in created)
        {
            Targets[Targets.IndexOf(row)] = new EditorTargetViewModel(target, isNew: false, takeOver: false);
        }

        // 3. Files of removed targets – only after the configuration no longer refers to them.
        var problems = new List<string>();
        foreach (var (target, removeMarker, removeArchive) in _removed)
        {
            // A review prepared for a removed target has nothing left to open.
            services.PendingReviews.Remove(target.Id);
            try
            {
                var saved = job;
                var report = await Task.Run(() => services.Cleanup.CleanTargetFolder(saved, target, removeMarker, removeArchive));
                problems.AddRange(report.NotCleaned.Concat(report.Errors));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"'{target.Name}': {ex.Message}");
            }
        }

        _removed.Clear();
        return problems.Count > 0
            ? new SaveResult(true, "Some files could not be removed", string.Join(Environment.NewLine, problems))
            : new SaveResult(true);
    }

    [RelayCommand]
    private void Cancel() => _shell.ShowDashboard();

    private void Touch()
    {
        _touched = true;
        Validate();
    }

    private void Validate()
    {
        if (Targets is null)
        {
            return;
        }

        var errors = _shell.Services.Config.Validate(BuildJob(), _shell.Services.Store.BaseDirectory).ToList();
        if (_shell.Services.IsReadOnly)
        {
            errors.Add("Settings are read-only: " + _shell.Services.StartupMessage);
        }

        CanSave = errors.Count == 0;

        // A new, untouched form shows no errors yet; Save stays disabled until it is valid.
        Errors.Clear();
        if (!_isNew || _touched || _shell.Services.IsReadOnly)
        {
            foreach (var error in errors)
            {
                Errors.Add(error);
            }
        }

        OnPropertyChanged(nameof(HasErrors));
    }
}
