using Amberkeep.App.Platform;
using Amberkeep.App.Services;
using Amberkeep.App.ViewModels;
using Amberkeep.App.Views;
using Amberkeep.Storage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Amberkeep.App;

public sealed partial class App : Application
{
    /// <summary>How long Quit waits for a running backup to stop and clean up.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private AppServices? _services;
    private MainViewModel? _main;
    private MainWindow? _window;
    private BackgroundRunner? _runner;
    private AutomationEngine? _automation;
    private TrayService? _tray;
    private bool _quitting;
    private bool _showingError;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Start(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        ErrorLog.Configure(AppStore.ExecutableDirectory);
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            ReportUnexpected(e.Exception, "User interface");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            ErrorLog.Write(e.Exception, "Background task");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                ErrorLog.Write(ex, "Unhandled");
            }
        };

        // The app lives on in the tray; it ends only through Quit (or closing the window when "close to tray" is off).
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var startHidden = desktop.Args?.Contains(Autostart.BackgroundArgument) == true;

        var services = AppServices.Create(AppStore.ExecutableDirectory, Autostart.ForCurrentProcess());
        _services = services;
        var autostartProblem = services.SyncAutostart();

        var main = new MainViewModel(services);
        _main = main;
        var window = new MainWindow { DataContext = main };
        _window = window;
        main.Dialogs = new OverlayDialogService(main, () => window);
        main.ExitRequested += (_, _) => Quit(force: true);

        var notifier = new ToastNotifier();
        _runner = new BackgroundRunner(services, notifier, review => Dispatcher.UIThread.Post(() =>
        {
            ShowWindow();
            main.OpenReview(review);
        }));
        _automation = new AutomationEngine(services, _runner.RunAsync);
        _tray = new TrayService(this, services, _runner, notifier, ShowWindow, () => Quit(force: false));

        window.Closing += (_, e) =>
        {
            // Sign-out, shutdown and the app's own Shutdown() must never be held up by the tray behaviour.
            if (_quitting || e.CloseReason != WindowCloseReason.WindowClosing)
            {
                StopForShutdown();
                return;
            }

            e.Cancel = true;
            if (services.Config.Settings.CloseToTray)
            {
                window.Hide();
            }
            else
            {
                Quit(force: false);
            }
        };

        // Windows sign-out or shutdown: stop a running backup safely and let the session end.
        desktop.ShutdownRequested += (_, _) => StopForShutdown();

        if (Program.Instance is { } instance)
        {
            instance.ShowRequested += (_, _) => Dispatcher.UIThread.Post(ShowWindow);
        }

        desktop.Exit += (_, _) =>
        {
            _automation?.Dispose();
            _tray?.Dispose();
        };

        main.Start();
        if (!startHidden)
        {
            ShowWindow();
        }

        if (autostartProblem is not null)
        {
            notifier.Show("Amberkeep", autostartProblem);
        }

        _automation.Start();
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private async void Quit(bool force)
    {
        if (_quitting || _desktop is null)
        {
            return;
        }

        if (!force && _services is { Gate.IsBusy: true } && _main is not null)
        {
            ShowWindow();
            if (!await _main.Dialogs.ConfirmAsync(
                    "Quit Amberkeep?",
                    "A backup is running. It stops safely; nothing is left half-written and the next run continues.",
                    "Quit",
                    destructive: true))
            {
                return;
            }
        }

        // A second Quit may have come in while the confirmation was open.
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        try
        {
            _automation?.Dispose();
            if (_services is not null)
            {
                // Cancels whatever holds the gate (also an automatic run) and lets it clean up its temporary files
                // and write its history record.
                await _services.Gate.StopAsync(StopTimeout);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Quit");
        }
        finally
        {
            // Whatever happened above, Amberkeep closes.
            _desktop.Shutdown();
        }
    }

    // No waiting here: the system ends the session soon; the run is cancelled and stops between two files.
    private void StopForShutdown()
    {
        _quitting = true;
        _automation?.Dispose();
        _ = _services?.Gate.StopAsync(TimeSpan.Zero);
    }

    private async void ReportUnexpected(Exception exception, string context)
    {
        ErrorLog.Write(exception, context);
        if (_showingError || _main is null || _quitting)
        {
            return;
        }

        _showingError = true;
        try
        {
            ShowWindow();
            await _main.Dialogs.ShowMessageAsync(
                "Something went wrong",
                $"{exception.Message}{Environment.NewLine}{Environment.NewLine}The details were saved to {AppStore.ErrorLogFileName} next to Amberkeep. Your backups are not affected by this message; if a backup was running, check its result in History.");
            _main.ShowDashboard();
        }
        finally
        {
            _showingError = false;
        }
    }
}
