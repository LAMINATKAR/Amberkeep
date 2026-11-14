using Avalonia;

namespace Amberkeep.App.Views;

/// <summary>Shrinks a window that would not fit on the screen's working area (e.g. a laptop with display scaling).</summary>
internal static class WindowFit
{
    /// <summary>Room for the title bar and the window frame, which are not part of the window's client size.</summary>
    internal const double FrameHeight = 40;

    /// <summary>
    /// The client size that fits into <paramref name="workingArea"/> (pixels; scaled by <paramref name="scaling"/>),
    /// never below <paramref name="minimum"/>, and the position that centers it; null when it already fits.
    /// </summary>
    public static (Size Size, PixelPoint Position)? Fit(Size desired, Size minimum, PixelRect workingArea, double scaling)
    {
        if (scaling <= 0 || workingArea.Width <= 0 || workingArea.Height <= 0)
        {
            return null;
        }

        var availableWidth = workingArea.Width / scaling;
        var availableHeight = workingArea.Height / scaling - FrameHeight;
        if (desired.Width <= availableWidth && desired.Height <= availableHeight)
        {
            return null;
        }

        var size = new Size(
            Math.Max(minimum.Width, Math.Min(desired.Width, availableWidth)),
            Math.Max(minimum.Height, Math.Min(desired.Height, availableHeight)));
        var x = workingArea.X + (int)Math.Max(0, (workingArea.Width - size.Width * scaling) / 2);
        var y = workingArea.Y + (int)Math.Max(0, (workingArea.Height - (size.Height + FrameHeight) * scaling) / 2);
        return (size, new PixelPoint(x, y));
    }
}
