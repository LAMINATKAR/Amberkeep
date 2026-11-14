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
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private AppServices? _services;
    private MainViewModel? _main;
    private MainWindow? _window;
    private BackgroundRunner? _runner;
    private AutomationEngine? _automation;
    private TrayService? _tray;
    private bool _quitting;

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
            if (_quitting)
            {
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

        if (Program.Instance is { } instance)
        {
            instance.ShowRequested += (_, _) => Dispatcher.UIThread.Post(ShowWindow);
        }

        desktop.Exit += (_, _) =>
        {
            _automation?.Dispose();
            _runner?.Cancel();
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

        _quitting = true;
        _runner?.Cancel();
        _desktop.Shutdown();
    }
}
