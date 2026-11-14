using Amberkeep.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;

namespace Amberkeep.Tests.App;

/// <summary>Window size on small screens and auto-scroll with the mouse wheel button.</summary>
public sealed class WindowBehaviorTests
{
    [Fact]
    public void Window_that_fits_is_left_alone()
    {
        Assert.Null(WindowFit.Fit(new Size(1120, 720), new Size(900, 560), new PixelRect(0, 0, 1920, 1040), 1.0));
    }

    [Fact]
    public void Window_shrinks_to_a_scaled_laptop_screen_and_is_centered()
    {
        // 1920 × 1080 at 150 % with a 48 px taskbar: 1280 × 688 DIP of working area.
        var fit = WindowFit.Fit(new Size(1120, 720), new Size(900, 560), new PixelRect(0, 0, 1920, 1032), 1.5);

        Assert.NotNull(fit);
        Assert.Equal(1120, fit.Value.Size.Width);
        Assert.Equal(1032 / 1.5 - WindowFit.FrameHeight, fit.Value.Size.Height, precision: 6);
        Assert.Equal((int)((1920 - 1120 * 1.5) / 2), fit.Value.Position.X);
        Assert.Equal(0, fit.Value.Position.Y);
    }

    [Fact]
    public void Window_never_shrinks_below_its_minimum()
    {
        var fit = WindowFit.Fit(new Size(1120, 720), new Size(900, 560), new PixelRect(0, 0, 1024, 600), 1.0);

        Assert.Equal(new Size(1024, 560), fit!.Value.Size);

        var tiny = WindowFit.Fit(new Size(1120, 720), new Size(900, 560), new PixelRect(0, 0, 800, 500), 1.0);
        Assert.Equal(new Size(900, 560), tiny!.Value.Size);
    }

    [Fact]
    public void Window_on_a_second_monitor_is_centered_on_that_monitor()
    {
        var fit = WindowFit.Fit(new Size(1120, 720), new Size(900, 560), new PixelRect(1920, 100, 1366, 728), 1.0);

        Assert.True(fit!.Value.Position.X >= 1920);
        Assert.True(fit.Value.Position.Y >= 100);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 0)]       // inside the dead zone
    [InlineData(-10, 0)]
    [InlineData(20, 80)]      // (20 - 10) × 8
    [InlineData(-20, -80)]
    [InlineData(100_000, MiddleClickScroll.MaxSpeed)]
    [InlineData(-100_000, -MiddleClickScroll.MaxSpeed)]
    public void Scroll_speed_grows_with_the_distance(double distance, double expected)
    {
        Assert.Equal(expected, MiddleClickScroll.Speed(distance));
    }

    [Fact]
    public async Task Wheel_click_and_mouse_move_scroll_the_area_under_the_pointer() => await HeadlessApp.Session.Dispatch(
        () =>
        {
            var (window, viewer, scroll) = CreateWindow();

            window.MouseDown(new Point(200, 150), MouseButton.Middle);
            window.MouseUp(new Point(200, 150), MouseButton.Middle);
            Assert.True(scroll.IsActive);

            // A plain click keeps scrolling: move down, let time pass.
            window.MouseMove(new Point(200, 250));
            scroll.Step(TimeSpan.FromMilliseconds(50));
            scroll.Step(TimeSpan.FromMilliseconds(100));
            Assert.True(viewer.Offset.Y > 0);

            // Moving above the start scrolls back up.
            var down = viewer.Offset.Y;
            window.MouseMove(new Point(200, 50));
            scroll.Step(TimeSpan.FromMilliseconds(150));
            Assert.True(viewer.Offset.Y < down);

            // The next click ends it.
            window.MouseDown(new Point(200, 50), MouseButton.Left);
            Assert.False(scroll.IsActive);
            window.Close();
        },
        TestContext.Current.CancellationToken);

    [Fact]
    public async Task Holding_and_dragging_ends_when_the_wheel_is_released() => await HeadlessApp.Session.Dispatch(
        () =>
        {
            var (window, viewer, scroll) = CreateWindow();

            window.MouseDown(new Point(200, 150), MouseButton.Middle);
            window.MouseMove(new Point(200, 300), RawInputModifiers.MiddleMouseButton);
            scroll.Step(TimeSpan.FromMilliseconds(100));
            Assert.True(viewer.Offset.Y > 0);

            window.MouseUp(new Point(200, 300), MouseButton.Middle);
            Assert.False(scroll.IsActive);
            window.Close();
        },
        TestContext.Current.CancellationToken);

    [Fact]
    public async Task Escape_ends_auto_scroll() => await HeadlessApp.Session.Dispatch(
        () =>
        {
            var (window, _, scroll) = CreateWindow();

            window.MouseDown(new Point(200, 150), MouseButton.Middle);
            window.MouseUp(new Point(200, 150), MouseButton.Middle);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

            Assert.False(scroll.IsActive);
            window.Close();
        },
        TestContext.Current.CancellationToken);

    [Fact]
    public async Task Nothing_happens_where_there_is_nothing_to_scroll() => await HeadlessApp.Session.Dispatch(
        () =>
        {
            var window = new Window { Width = 400, Height = 300, Content = new ScrollViewer { Content = new Border { Height = 100 } } };
            var scroll = MiddleClickScroll.Attach(window);
            window.Show();
            window.UpdateLayout();

            window.MouseDown(new Point(200, 50), MouseButton.Middle);

            Assert.False(scroll.IsActive);
            window.Close();
        },
        TestContext.Current.CancellationToken);

    private static (Window Window, ScrollViewer Viewer, MiddleClickScroll Scroll) CreateWindow()
    {
        var viewer = new ScrollViewer { Content = new Border { Height = 5000, Background = Avalonia.Media.Brushes.Gray } };
        var window = new Window { Width = 400, Height = 300, Content = viewer };
        var scroll = MiddleClickScroll.Attach(window);
        window.Show();
        window.UpdateLayout();
        return (window, viewer, scroll);
    }
}
