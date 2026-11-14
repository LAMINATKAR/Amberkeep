using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Amberkeep.App.Views;

// Views have no logic of their own; everything lives in the view models.

public sealed partial class MainWindow : Window
{
    private bool _fitted;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        MiddleClickScroll.Attach(this);
        Opened += (_, _) => FitToScreen();
    }

    /// <summary>
    /// On small screens (laptops with display scaling) the default size would not fit. Done once, on the
    /// first show; later the window keeps whatever size the user gave it (also after hiding to the tray).
    /// </summary>
    private void FitToScreen()
    {
        if (_fitted)
        {
            return;
        }

        _fitted = true;
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
        {
            return;
        }

        if (WindowFit.Fit(new Avalonia.Size(Width, Height), new Avalonia.Size(MinWidth, MinHeight), screen.WorkingArea, screen.Scaling) is { } fit)
        {
            Width = fit.Size.Width;
            Height = fit.Size.Height;
            Position = fit.Position;
        }
    }
}

/// <summary>The Amberkeep logo (<see cref="Platform.LogoArt"/>); 40 × 40 unless sized otherwise.</summary>
public sealed class LogoView : UserControl
{
    public LogoView()
    {
        Width = 40;
        Height = 40;
        Content = Platform.LogoArt.Create();
    }
}

public sealed partial class DashboardView : UserControl
{
    public DashboardView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class JobEditorView : UserControl
{
    public JobEditorView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class DiffView : UserControl
{
    public DiffView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class RunView : UserControl
{
    public RunView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class BusyView : UserControl
{
    public BusyView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class OneTimeRunView : UserControl
{
    public OneTimeRunView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class ConfirmDialogView : UserControl
{
    public ConfirmDialogView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class HistoryView : UserControl
{
    public HistoryView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class ArchiveView : UserControl
{
    public ArchiveView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => AvaloniaXamlLoader.Load(this);
}
