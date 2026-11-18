using System.Collections.ObjectModel;
using Amberkeep.App.Services;
using Amberkeep.Core.Execution;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.History;
using Amberkeep.Storage.Targets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberkeep.App.ViewModels;

/// <summary>Start page: all backup jobs with the state of their targets (SPEC 6.1).</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IShell shell;

    // Background runs report from other threads; bound properties change on the thread that created us.
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public DashboardViewModel(IShell shell)
    {
        this.shell = shell;
        _banner = shell.Services.StartupMessage;
        shell.Services.PendingReviews.Changed += (_, _) => OnUi(UpdateAutomationState);
        shell.Services.ConfigChanged += (_, _) => OnUi(UpdateAutomationState);
        UpdateAutomationState();
    }

    public ObservableCollection<JobCardViewModel> Jobs { get; } = [];

    [ObservableProperty]
    private string? _pendingText;

    [ObservableProperty]
    private bool _isPaused;

    /// <summary>
    /// The backup page last opened, until its result is closed with Done. The user may leave it while it runs;
    /// the dashboard then shows its progress (or its result) with a way back.
    /// </summary>
    [ObservableProperty]
    private RunViewModel? _activeRun;

    /// <summary>Called whenever a backup page is shown.</summary>
    internal void Track(RunViewModel run)
    {
        if (ReferenceEquals(ActiveRun, run))
        {
            return;
        }

        ActiveRun = run;
        run.Finished += (_, _) =>
        {
            // The user waits on the dashboard: "Last backup" changes.
            if (ReferenceEquals(ActiveRun, run) && shell is MainViewModel { CurrentPage: DashboardViewModel })
            {
                _ = RefreshAsync();
            }
        };
        run.Closed += (_, _) =>
        {
            if (ReferenceEquals(ActiveRun, run))
            {
                ActiveRun = null;
            }
        };
    }

    /// <summary>Goes back to the backup page (its progress, or its result).</summary>
    [RelayCommand]
    private void ShowRun()
    {
        if (ActiveRun is { } run)
        {
            shell.Show(run);
        }
    }

    /// <summary>Opens the oldest backup waiting for review.</summary>
    [RelayCommand]
    private void Review()
    {
        if (shell.Services.PendingReviews.Items.FirstOrDefault() is { } review && shell is MainViewModel main)
        {
            main.OpenReview(review);
        }
    }

    [RelayCommand]
    private void Resume()
    {
        var config = shell.Services.Config;
        try
        {
            shell.Services.Save(config with { Settings = config.Settings with { AutomaticRunsPaused = false } });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _ = shell.Dialogs.ShowMessageAsync("Settings not saved", ex.Message);
        }
    }

    private void UpdateAutomationState()
    {
        var pending = shell.Services.PendingReviews.Items;
        PendingText = pending.Count switch
        {
            0 => null,
            1 => $"{pending[0].Title} has {Format.Count(pending[0].Changes, "change", "changes")} waiting for your review.",
            _ => $"{pending.Count} backups are waiting for your review.",
        };
        shell.Services.Activity.SetReviewsWaiting(pending.Count > 0);
        IsPaused = shell.Services.Config.Settings.AutomaticRunsPaused && shell.Services.Config.Jobs.Any(j => j.Trigger.IsAutomatic);
    }

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    [ObservableProperty]
    private bool _hasJobs;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private string? _banner;

    public bool BannerIsError => shell.Services.StartupMessageIsError;

    public bool IsReadOnly => shell.Services.IsReadOnly;

    public bool CanEdit => !IsReadOnly;

    /// <summary>Rebuilds the cards and checks every target (in the background).</summary>
    public async Task RefreshAsync()
    {
        // Refreshes can overlap (save, run finished, reload); only the newest may replace the cards.
        var generation = ++_refreshGeneration;
        IsRefreshing = true;
        try
        {
            var config = shell.Services.Config;
            var cards = await Task.Run(() =>
            {
                var history = shell.Services.History.List();
                return config.Jobs.Select(job => new JobCardViewModel(shell, this, job, history)).ToList();
            });
            if (generation != _refreshGeneration)
            {
                return;
            }

            // A refresh in the middle of dragging a card ends the drag; the saved order is shown.
            _orderBeforeReorder = null;
            Jobs.Clear();
            foreach (var card in cards)
            {
                Jobs.Add(card);
            }

            HasJobs = Jobs.Count > 0;
        }
        finally
        {
            if (generation == _refreshGeneration)
            {
                IsRefreshing = false;
            }
        }
    }

    private int _refreshGeneration;

    /// <summary>The order of the cards when dragging started; null when no card is being dragged.</summary>
    private List<JobCardViewModel>? _orderBeforeReorder;

    /// <summary>Moves a card while it is dragged. The new order is saved only by <see cref="CommitOrder"/>.</summary>
    public void MoveJob(JobCardViewModel card, int toIndex)
    {
        var from = Jobs.IndexOf(card);
        if (!CanEdit || from < 0 || toIndex < 0 || toIndex >= Jobs.Count || from == toIndex)
        {
            return;
        }

        _orderBeforeReorder ??= Jobs.ToList();
        Jobs.Move(from, toIndex);
    }

    /// <summary>Saves the order of the cards after dragging. Run all, the tray menu and History follow it.</summary>
    public void CommitOrder()
    {
        if (_orderBeforeReorder is not { } before)
        {
            return;
        }

        _orderBeforeReorder = null;
        if (before.SequenceEqual(Jobs))
        {
            return;
        }

        var order = Jobs.Select(card => card.Job.Id).ToList();
        try
        {
            shell.Services.Update(config => config.WithJobOrder(order));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Restore(before);
            _ = shell.Dialogs.ShowMessageAsync("Settings not saved", ex.Message);
        }
    }

    /// <summary>Puts the cards back where they were before dragging (Esc, or the window lost focus).</summary>
    public void CancelReorder()
    {
        if (_orderBeforeReorder is { } before)
        {
            _orderBeforeReorder = null;
            Restore(before);
        }
    }

    private void Restore(List<JobCardViewModel> order)
    {
        for (var i = 0; i < order.Count && i < Jobs.Count; i++)
        {
            var from = Jobs.IndexOf(order[i]);
            if (from >= 0 && from != i)
            {
                Jobs.Move(from, i);
            }
        }
    }

    [RelayCommand]
    private Task ReloadAsync() => RefreshAsync();

    [RelayCommand]
    private void NewJob() => shell.Show(new JobEditorViewModel(shell, null));

    [RelayCommand]
    private void OneTimeRun() => shell.Show(new OneTimeRunViewModel(shell));

    [RelayCommand]
    private Task ShowHistoryAsync()
    {
        // The user looks at what happened, so the tray stops asking for attention about failed runs.
        shell.Services.Activity.ClearProblem();
        var history = new HistoryViewModel(shell);
        shell.Show(history);
        return history.LoadAsync();
    }

    [RelayCommand]
    private void ShowSettings() => shell.Show(new SettingsViewModel(shell));

    internal Task ShowArchiveAsync(JobConfig job, TargetConfig target, string path)
    {
        var archive = new ArchiveViewModel(shell, job, target, path);
        shell.Show(archive);
        return archive.LoadAsync();
    }

    [RelayCommand]
    private void DismissBanner() => Banner = null;

    /// <summary>Runs every connected target of every job, one after another.</summary>
    [RelayCommand]
    private async Task RunAllAsync()
    {
        var queue = new Queue<(JobConfig Job, TargetConfig Target)>(
            Jobs.SelectMany(card => card.Targets.Where(t => t.IsConnected).Select(t => (card.Job, t.Target))));
        if (queue.Count == 0)
        {
            await shell.Dialogs.ShowMessageAsync("Nothing to run", "No backup target is connected.");
            return;
        }

        await RunNextAsync();

        async Task RunNextAsync()
        {
            if (!queue.TryDequeue(out var next))
            {
                shell.ShowDashboard();
                return;
            }

            await new JobFlow(shell).StartAsync(next.Job, next.Target, runWithoutReview: true, onFinished: () => _ = ContinueAsync());
        }

        // The next step starts from a callback; its errors are reported instead of disappearing with the task.
        async Task ContinueAsync()
        {
            try
            {
                await RunNextAsync();
            }
            catch (Exception ex)
            {
                Services.ErrorLog.Write(ex, "Run all");
                await shell.Dialogs.ShowMessageAsync("Run all stopped", ex.Message);
                shell.ShowDashboard();
            }
        }
    }

    internal async Task DeleteJobAsync(JobConfig job)
    {
        var answers = await shell.Dialogs.ConfirmWithOptionsAsync(
            $"Delete '{job.Name}'?",
            "The backup is removed from Amberkeep. Backed-up files stay where they are, and its past runs stay in History.",
            "Delete",
            [new DialogOption("Also remove Amberkeep files (marker) from connected targets", true),
             new DialogOption("Also delete archived old versions on connected targets"),
             new DialogOption("Also delete its runs from History")],
            destructive: true);
        if (answers is null)
        {
            return;
        }

        // Not while a backup runs: its marker, archive and temporary files may be what gets deleted.
        if (!shell.Services.Gate.TryEnter())
        {
            await JobFlow.ShowBusyAsync(shell);
            return;
        }

        Amberkeep.Storage.Cleanup.CleanupReport? report = null;
        string? failure = null;
        try
        {
            var saved = shell.Services.Config.FindJob(job.Id) ?? job;
            foreach (var target in saved.Targets)
            {
                shell.Services.PendingReviews.Remove(target.Id);
            }

            // Saved first (under the save lock): if that fails, nothing has been removed yet.
            shell.Services.Update(config => config.WithoutJob(saved.Id));
            report = await Task.Run(() => shell.Services.Cleanup.CleanUpRemovedJob(saved, answers[0], answers[0] && answers[1], answers[2]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            failure = ex.Message;
        }
        finally
        {
            shell.Services.Gate.Exit();
        }

        if (failure is not null)
        {
            await shell.Dialogs.ShowMessageAsync("The backup could not be deleted", failure);
        }
        else if (report is not null)
        {
            await ShowReportAsync(report);
        }

        await RefreshAsync();
    }

    internal async Task LocateAsync(JobConfig job, TargetConfig target)
    {
        var folder = await shell.Dialogs.PickFolderAsync($"Where is '{target.Name}'?");
        if (folder is null)
        {
            return;
        }

        try
        {
            var result = shell.Services.Registry.Relink(shell.Services.Config, job, target, folder, confirmed: false);
            if (result.ConfirmationNeeded is { } question)
            {
                if (!await shell.Dialogs.ConfirmAsync("Use this folder?", question, "Use it"))
                {
                    return;
                }

                result = shell.Services.Registry.Relink(shell.Services.Config, job, target, folder, confirmed: true);
            }

            // Only the target's location changes; the rest of the job is taken as it is saved now.
            var located = result.Target!;
            shell.Services.Update(config => config.FindJob(job.Id) is { } current && current.FindTarget(located.Id) is not null
                ? config.WithJob(current.WithTarget(located))
                : config);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await shell.Dialogs.ShowMessageAsync("Cannot use this folder", ex.Message);
        }

        await RefreshAsync();
    }

    internal async Task ShowReportAsync(Amberkeep.Storage.Cleanup.CleanupReport report)
    {
        var lines = report.NotCleaned.Concat(report.Errors).ToList();
        if (lines.Count > 0)
        {
            await shell.Dialogs.ShowMessageAsync("Some files could not be removed", string.Join(Environment.NewLine, lines));
        }
    }
}

public sealed partial class JobCardViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly DashboardViewModel _dashboard;

    public JobCardViewModel(IShell shell, DashboardViewModel dashboard, JobConfig job, IReadOnlyList<RunRecord> history)
    {
        _shell = shell;
        _dashboard = dashboard;
        Job = job;
        Targets = new ObservableCollection<TargetRowViewModel>(job.Targets.Select(t => new TargetRowViewModel(shell, dashboard, job, t, history)));
    }

    public JobConfig Job { get; }

    public string Name => Job.Name;

    public string SourcePath => Job.SourcePath;

    /// <summary>"Only: …" when the backup covers selected folders and files of the source; null for the whole folder.</summary>
    public string? SelectionText => Format.Selection(Job.IncludedPaths);

    public ObservableCollection<TargetRowViewModel> Targets { get; }

    public bool HasTargets => Targets.Count > 0;

    public bool ArchiveEnabled => Job.Archive.Enabled;

    public bool RequiresApproval => Job.RequireApproval;

    /// <summary>"On connect · Daily 20:00", or null when the job only runs manually.</summary>
    public string? TriggerText => Format.Trigger(Job.Trigger);

    public bool CanEdit => !_shell.Services.IsReadOnly;

    /// <summary>The card is being dragged to a new place.</summary>
    [ObservableProperty]
    private bool _isDragging;

    [RelayCommand]
    private void Edit() => _shell.Show(new JobEditorViewModel(_shell, Job));

    [RelayCommand]
    private Task DeleteAsync() => _dashboard.DeleteJobAsync(Job);
}

public enum TargetDisplayState
{
    Connected,
    NotConnected,
    Unverified,
}

public sealed partial class TargetRowViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly DashboardViewModel _dashboard;

    /// <summary>Checks the target right away; call off the UI thread.</summary>
    public TargetRowViewModel(IShell shell, DashboardViewModel dashboard, JobConfig job, TargetConfig target, IReadOnlyList<RunRecord> history)
    {
        _shell = shell;
        _dashboard = dashboard;
        Job = job;
        Target = target;

        var services = shell.Services;
        var status = services.Locator.Locate(target);
        State = status.State switch
        {
            TargetState.Connected => TargetDisplayState.Connected,
            TargetState.Unverified => TargetDisplayState.Unverified,
            _ => TargetDisplayState.NotConnected,
        };
        Path = status.Path ?? target.LastKnownPath;
        StateText = State switch
        {
            TargetDisplayState.Connected => "Connected",
            TargetDisplayState.Unverified => "Not verified",
            _ => "Not connected",
        };
        Reason = status.Reason;

        if (status.IsConnected && services.FileSystem.GetAvailableFreeSpace(Path) is { } free)
        {
            FreeSpaceText = $"{Format.Bytes(free)} free";
        }

        HasArchive = status.IsConnected
                     && services.FileSystem.DirectoryExists(Core.Execution.ArchiveCleaner.ArchiveRoot(Path));

        var last = history.FirstOrDefault(r => r.JobId == job.Id && r.TargetId == target.Id);
        LastRunText = last is null
            ? "Never backed up"
            : $"Last backup {Format.When(last.StartedUtc, services.Time.GetUtcNow())} – {Format.Outcome(last.Outcome).ToLowerInvariant()}";
        LastRunFailed = last is { Outcome: not ExecutionOutcome.Completed };
    }

    public JobConfig Job { get; }

    public TargetConfig Target { get; }

    public string Name => Target.Name;

    public string Path { get; }

    public TargetDisplayState State { get; }

    public string StateText { get; }

    public string? Reason { get; }

    public bool HasReason => Reason is not null;

    public string? FreeSpaceText { get; }

    public string LastRunText { get; }

    public bool LastRunFailed { get; }

    public bool IsConnected => State == TargetDisplayState.Connected;

    public bool CanLocate => !IsConnected && !_shell.Services.IsReadOnly;

    /// <summary>The target is connected and has archived files to browse.</summary>
    public bool HasArchive { get; }

    [RelayCommand]
    private Task OpenArchiveAsync() => _dashboard.ShowArchiveAsync(Job, Target, Path);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task AnalyzeAsync() => new JobFlow(_shell).StartAsync(Job, Target, runWithoutReview: false);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task RunAsync() => new JobFlow(_shell).StartAsync(Job, Target, runWithoutReview: true);

    [RelayCommand]
    private Task LocateAsync() => _dashboard.LocateAsync(Job, Target);
}
