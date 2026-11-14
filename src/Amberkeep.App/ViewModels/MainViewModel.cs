using Amberkeep.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amberkeep.App.ViewModels;

/// <summary>What child view models use to navigate and to reach services and dialogs.</summary>
public interface IShell
{
    AppServices Services { get; }

    IDialogService Dialogs { get; }

    void Show(object page);

    void ShowDashboard();

    /// <summary>Closes the application (after "Clean up everything").</summary>
    void RequestExit();
}

/// <summary>Root of the window: the current page and the dialog shown over it.</summary>
public sealed partial class MainViewModel : ObservableObject, IShell
{
    // One dialog at a time; a second request waits until the first one is answered.
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    public MainViewModel(AppServices services, IDialogService? dialogs = null)
    {
        Services = services;
        Dialogs = dialogs ?? NoDialogs.Instance;
        Dashboard = new DashboardViewModel(this);
    }

    public AppServices Services { get; }

    /// <summary>Set by the application once the window exists, so that dialogs and pickers have an owner.</summary>
    public IDialogService Dialogs { get; set; }

    public DashboardViewModel Dashboard { get; }

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private ConfirmDialogViewModel? _dialog;

    /// <summary>Raised by <see cref="RequestExit"/>; the application shuts down.</summary>
    public event EventHandler? ExitRequested;

    public void Start() => ShowDashboard();

    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Opens a backup that an automatic run prepared for review (analyzed again, so the list is current).</summary>
    /// <remarks>
    /// Not while the user is busy (an editor or a one-time run that may have unsaved input, a diff, a backup page,
    /// an open dialog): then the window only comes up and the review waits in the dashboard bar.
    /// The review leaves the bar only when its analysis really starts.
    /// </remarks>
    public void OpenReview(PendingReview review)
    {
        var job = Services.Config.FindJob(review.Job.Id);
        if (job?.FindTarget(review.Target.Id) is not { } target)
        {
            Services.PendingReviews.Remove(review.Target.Id);
            return;
        }

        // On the dashboard a running backup is left to JobFlow: it says so and the review stays in the bar.
        // Elsewhere (e.g. a restore running on the archive page) the page is kept as it is.
        var busy = Dialog is not null
                   || (Services.Gate.IsBusy && CurrentPage is not DashboardViewModel)
                   || CurrentPage is JobEditorViewModel or OneTimeRunViewModel or DiffViewModel or RunViewModel or BusyViewModel;
        if (busy)
        {
            return;
        }

        _ = new JobFlow(this).StartAsync(
            job,
            target,
            runWithoutReview: false,
            onStarted: () => Services.PendingReviews.Remove(review.Target.Id));
    }

    public void Show(object page) => CurrentPage = page;

    public void ShowDashboard()
    {
        CurrentPage = Dashboard;
        _ = Dashboard.RefreshAsync();
    }

    /// <summary>Shows a dialog over the page and waits for the answer.</summary>
    internal async Task<bool> ShowDialogAsync(ConfirmDialogViewModel dialog)
    {
        await _dialogGate.WaitAsync().ConfigureAwait(true);
        Dialog = dialog;
        try
        {
            return await dialog.Result.ConfigureAwait(true);
        }
        finally
        {
            Dialog = null;
            _dialogGate.Release();
        }
    }
}
