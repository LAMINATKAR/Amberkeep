using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amberkeep.App.Views;

/// <summary>
/// Windows-style auto-scroll: press the mouse wheel over a scrollable area and move the mouse; the further from
/// the starting point, the faster it scrolls. Hold-and-drag ends when the wheel is released; a plain click keeps
/// scrolling until the next click, a wheel turn, Esc or leaving the window.
/// </summary>
internal sealed class MiddleClickScroll
{
    /// <summary>No scrolling within this distance (DIP) of the starting point.</summary>
    public const double DeadZone = 10;

    /// <summary>Scrolling speed in DIP per second for each DIP beyond the dead zone.</summary>
    public const double Gain = 8;

    /// <summary>The fastest scrolling speed, DIP per second.</summary>
    public const double MaxSpeed = 4000;

    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private ScrollViewer? _viewer;
    private Point _origin;
    private Point _current;
    private bool _dragged;
    private Cursor? _previousCursor;
    private TimeSpan _lastStep;

    private MiddleClickScroll(Window window)
    {
        _window = window;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Step(_clock.Elapsed));
        window.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.Deactivated += (_, _) => Stop();
    }

    public bool IsActive => _viewer is not null;

    public static MiddleClickScroll Attach(Window window) => new(window);

    /// <summary>Scrolling speed (DIP per second) along one axis for a pointer this far (DIP) from the starting point.</summary>
    public static double Speed(double distance)
    {
        var beyond = Math.Abs(distance) - DeadZone;
        return beyond <= 0 ? 0 : Math.Sign(distance) * Math.Min(MaxSpeed, beyond * Gain);
    }

    /// <summary>Scrolls by the distance covered since the previous step (<paramref name="elapsed"/> since the start).</summary>
    internal void Step(TimeSpan elapsed)
    {
        if (_viewer is null)
        {
            return;
        }

        // The page changed under the pointer (e.g. a notification opened another page).
        if (TopLevel.GetTopLevel(_viewer) is null)
        {
            Stop();
            return;
        }

        var seconds = (elapsed - _lastStep).TotalSeconds;
        _lastStep = elapsed;
        if (seconds <= 0)
        {
            return;
        }

        // Long gaps (a busy UI thread) must not turn into a jump.
        seconds = Math.Min(seconds, 0.1);
        var delta = _current - _origin;
        var offset = _viewer.Offset;
        _viewer.Offset = new Vector(offset.X + Speed(delta.X) * seconds, offset.Y + Speed(delta.Y) * seconds);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsActive)
        {
            // Any click ends auto-scroll and does nothing else.
            Stop();
            e.Handled = true;
            return;
        }

        if (!e.GetCurrentPoint(_window).Properties.IsMiddleButtonPressed || FindScrollable(e.Source as Visual) is not { } viewer)
        {
            return;
        }

        _viewer = viewer;
        _origin = _current = e.GetPosition(_window);
        _dragged = false;
        _previousCursor = _window.Cursor;
        _window.Cursor = new Cursor(StandardCursorType.SizeAll);
        _lastStep = TimeSpan.Zero;
        _clock.Restart();
        _timer.Start();
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!IsActive)
        {
            return;
        }

        _current = e.GetPosition(_window);
        var delta = _current - _origin;
        if (e.GetCurrentPoint(_window).Properties.IsMiddleButtonPressed && Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)) > DeadZone)
        {
            _dragged = true;
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (IsActive && e.InitialPressMouseButton == MouseButton.Middle)
        {
            // After holding and dragging, releasing the wheel ends it; after a plain click it keeps going.
            if (_dragged)
            {
                Stop();
            }

            e.Handled = true;
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e) => Stop();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsActive && e.Key == Key.Escape)
        {
            Stop();
            e.Handled = true;
        }
    }

    private void Stop()
    {
        if (!IsActive)
        {
            return;
        }

        _timer.Stop();
        _clock.Stop();
        _viewer = null;
        _window.Cursor = _previousCursor;
        _previousCursor = null;
    }

    /// <summary>The innermost scroll viewer under the pointer that has something to scroll.</summary>
    private static ScrollViewer? FindScrollable(Visual? source) =>
        source?.GetSelfAndVisualAncestors()
            .OfType<ScrollViewer>()
            .FirstOrDefault(v => v.Extent.Height > v.Viewport.Height + 0.5 || v.Extent.Width > v.Viewport.Width + 0.5);
}
