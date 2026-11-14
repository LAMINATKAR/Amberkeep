using Amberkeep.App.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Amberkeep.App.Platform;

/// <summary>The icon in the notification area with its menu and live status (SPEC 4.6).</summary>
public sealed class TrayService : IDisposable
{
    private readonly Application _application;
    private readonly AppServices _services;
    private readonly BackgroundRunner _runner;
    private readonly Action _showWindow;
    private readonly Action _quit;
    private readonly INotifier _notifier;
    private readonly TrayIcon _icon;
    private (ActivityKind Kind, int Percent, bool Attention) _drawn = (ActivityKind.Idle, -1, false);

    public TrayService(Application application, AppServices services, BackgroundRunner runner, INotifier notifier, Action showWindow, Action quit)
    {
        _notifier = notifier;
        _application = application;
        _services = services;
        _runner = runner;
        _showWindow = showWindow;
        _quit = quit;

        _icon = new TrayIcon { ToolTipText = "Amberkeep", Icon = TrayIconRenderer.Render(services.Activity.State) };
        _icon.Clicked += (_, _) => _showWindow();
        TrayIcon.SetIcons(application, [_icon]);

        services.Activity.Changed += OnActivityChanged;
        services.ConfigChanged += OnConfigChanged;
        RebuildMenu();
    }

    public void Dispose()
    {
        _services.Activity.Changed -= OnActivityChanged;
        _services.ConfigChanged -= OnConfigChanged;
        _icon.IsVisible = false;
        TrayIcon.SetIcons(_application, []);
        _icon.Dispose();
    }

    private void OnConfigChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RebuildMenu);

    private void OnActivityChanged(object? sender, ActivityState state) => Dispatcher.UIThread.Post(() =>
    {
        _icon.ToolTipText = state.Text;

        // Redraw only when something visible changed (the ring moves in 5 % steps).
        var key = (state.Kind, state.Kind == ActivityKind.Running ? (int)(state.Percent / 5) * 5 : 0, state.NeedsAttention);
        if (key != _drawn)
        {
            _drawn = key;
            _icon.Icon = TrayIconRenderer.Render(state with { Percent = key.Item2 });
        }
    });

    private void RebuildMenu()
    {
        var config = _services.Config;
        var menu = new NativeMenu();
        menu.Items.Add(Item("Open Amberkeep", _showWindow));

        var runNow = new NativeMenuItem("Run now") { Menu = new NativeMenu(), IsEnabled = config.Jobs.Count > 0 };
        foreach (var job in config.Jobs)
        {
            runNow.Menu.Items.Add(Item(job.Name, () => _ = RunAsync(job.Id)));
        }

        menu.Items.Add(runNow);
        menu.Items.Add(Item("Run all now", () => _ = RunAsync(null)));
        menu.Items.Add(new NativeMenuItemSeparator());

        var pause = new NativeMenuItem("Pause automatic runs")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = config.Settings.AutomaticRunsPaused,
        };
        pause.Click += (_, _) => TogglePause();
        menu.Items.Add(pause);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Quit", _quit));
        _icon.Menu = menu;
    }

    private void TogglePause()
    {
        var config = _services.Config;
        try
        {
            _services.Save(config with { Settings = config.Settings with { AutomaticRunsPaused = !config.Settings.AutomaticRunsPaused } });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Read-only settings; the menu shows the unchanged state after the rebuild.
        }

        RebuildMenu();
    }

    /// <summary>Runs connected targets of one job (or of all jobs) like an automatic run.</summary>
    private async Task RunAsync(Guid? jobId)
    {
        var started = 0;
        var upToDate = 0;
        foreach (var job in _services.Config.Jobs.Where(j => jobId is null || j.Id == jobId))
        {
            foreach (var target in job.Targets.Where(t => _services.Locator.Locate(t).IsConnected))
            {
                started++;
                switch (await _runner.RunAsync(job, target).ConfigureAwait(true))
                {
                    case BackgroundRunResult.Busy:
                        _notifier.Show("A backup is already running", "Try again when it has finished.");
                        return;
                    case BackgroundRunResult.UpToDate:
                        upToDate++;
                        break;
                }
            }
        }

        // Runs with changes notify about themselves; tell the user about the quiet outcomes too.
        if (started == 0)
        {
            _notifier.Show("Nothing to run", "No backup target is connected.");
        }
        else if (upToDate == started)
        {
            _notifier.Show("Everything is up to date", "There were no changes to back up.");
        }
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }
}
