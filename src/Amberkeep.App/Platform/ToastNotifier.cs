using Amberkeep.App.Services;
using Amberkeep.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Amberkeep.App.Platform;

/// <summary>
/// Notifications as a small window in the corner of the screen. No operating-system notification
/// services are used, so nothing has to be registered outside the program folder.
/// </summary>
public sealed class ToastNotifier : INotifier
{
    private static readonly TimeSpan Visible = TimeSpan.FromSeconds(8);
    private NotificationWindow? _current;

    public void Show(string title, string message, Action? onClick = null) => Dispatcher.UIThread.Post(() =>
    {
        _current?.Close();
        var window = new NotificationWindow(title, message, onClick);
        _current = window;
        window.Closed += (_, _) =>
        {
            if (_current == window)
            {
                _current = null;
            }
        };
        window.Show();
        DispatcherTimer.RunOnce(window.Close, Visible);
    });
}

public sealed class NotificationWindow : Window
{
    public NotificationWindow(string title, string message, Action? onClick)
    {
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        Content = new Border
        {
            Classes = { "card" },
            Width = 360,
            Padding = new Thickness(16, 12),
            Margin = new Thickness(8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new DockPanel
            {
                Children =
                {
                    new LogoView { Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, [DockPanel.DockProperty] = Dock.Left },
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" } },
                        },
                    },
                },
            },
        };

        PointerPressed += (_, _) =>
        {
            Close();
            onClick?.Invoke();
        };
        Opened += (_, _) => PlaceInCorner();
    }

    private void PlaceInCorner()
    {
        if (Screens.Primary is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(Bounds.Width * scale);
        var height = (int)(Bounds.Height * scale);
        Position = new PixelPoint(area.Right - width - (int)(12 * scale), area.Bottom - height - (int)(12 * scale));
    }
}
