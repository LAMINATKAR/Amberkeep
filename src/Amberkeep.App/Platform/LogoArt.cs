using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Path = Avalonia.Controls.Shapes.Path;

namespace Amberkeep.App.Platform;

/// <summary>
/// The Amberkeep logo: a faceted amber stone with a small preserved "drop" inside.
/// The single source of the artwork for the in-app logo, the tray icon and the generated application icon.
/// </summary>
public static class LogoArt
{
    /// <summary>Sizes up to this many pixels drop the thin facet lines, which would only blur.</summary>
    public const int SimpleUpTo = 24;

    private const double DesignSize = 64;
    private static readonly Uri IconUri = new("avares://Amberkeep/Assets/amberkeep.ico");

    // Immutable brushes can be shared between controls and threads; the tray icon is redrawn on every progress step.
    private static readonly IBrush StoneFill = new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Color.Parse("#F7CF6B")), new ImmutableGradientStop(0.55, Color.Parse("#E09A26")), new ImmutableGradientStop(1, Color.Parse("#A9600E"))],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
        endPoint: new RelativePoint(1, 1, RelativeUnit.Relative));

    private static readonly IBrush Facet = new ImmutableSolidColorBrush(Color.Parse("#FFF1C9"));
    private static readonly IBrush Table = new ImmutableSolidColorBrush(Color.Parse("#FFF3D1"));
    private static readonly IBrush Drop = new ImmutableSolidColorBrush(Color.Parse("#5A3307"));

    /// <summary>The logo, scaled to whatever size the returned control is given.</summary>
    public static Control Create(bool detailed = true)
    {
        var canvas = new Canvas { Width = DesignSize, Height = DesignSize };
        canvas.Children.Add(new Path { Data = Geometry.Parse("M20,6 L44,6 L58,24 L32,60 L6,24 Z"), Fill = StoneFill });

        if (detailed)
        {
            canvas.Children.Add(new Path { Data = Geometry.Parse("M6,24 L58,24"), Stroke = Facet, StrokeThickness = 1.6, Opacity = 0.7 });
            canvas.Children.Add(new Path { Data = Geometry.Parse("M20,6 L26,24 L32,60 M44,6 L38,24 L32,60"), Stroke = Facet, StrokeThickness = 1.2, Opacity = 0.45 });
        }

        canvas.Children.Add(new Path { Data = Geometry.Parse("M20,6 L44,6 L38,24 L26,24 Z"), Fill = Table, Opacity = 0.35 });
        canvas.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Drop,
            Opacity = 0.75,
            [Canvas.LeftProperty] = 28.0,
            [Canvas.TopProperty] = 32.0,
        });

        return new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
    }

    /// <summary>The application icon (generated from this artwork by tools/Amberkeep.IconGen) for windows.</summary>
    public static WindowIcon ApplicationIcon()
    {
        using var stream = AssetLoader.Open(IconUri);
        return new WindowIcon(stream);
    }

    /// <summary>Renders the logo to a square bitmap of <paramref name="pixels"/> × <paramref name="pixels"/>.</summary>
    public static Bitmap Render(int pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels, 1);
        var logo = Create(detailed: pixels > SimpleUpTo);
        logo.Width = pixels;
        logo.Height = pixels;
        return RenderControl(logo, pixels);
    }

    /// <summary>Lays out a control at a fixed square size and renders it to a bitmap.</summary>
    internal static RenderTargetBitmap RenderControl(Control control, int pixels)
    {
        control.Measure(new Size(pixels, pixels));
        control.Arrange(new Rect(0, 0, pixels, pixels));
        var bitmap = new RenderTargetBitmap(new PixelSize(pixels, pixels));
        bitmap.Render(control);
        return bitmap;
    }
}
