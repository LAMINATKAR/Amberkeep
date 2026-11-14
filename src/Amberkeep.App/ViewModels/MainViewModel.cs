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
