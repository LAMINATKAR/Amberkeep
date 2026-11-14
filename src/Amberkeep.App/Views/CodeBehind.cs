using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Amberkeep.App.Views;

// Views have no logic of their own; everything lives in the view models.

public sealed partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
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
