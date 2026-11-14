using System.Globalization;
using Amberkeep.App.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Path = Avalonia.Controls.Shapes.Path;

namespace Amberkeep.App.Platform;

/// <summary>Draws the tray icon: the amber stone, a progress ring while backing up and small status dots.</summary>
public static class TrayIconRenderer
{
    private const int Size = 64;
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#E09A26"));
    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#55808080"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#3B82C4"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#D2453F"));

    public static Bitmap RenderBitmap(ActivityState state)
    {
        var canvas = new Canvas { Width = Size, Height = Size };
        var running = state.Kind == ActivityKind.Running;

        // The stone, smaller when a ring is drawn around it.
        var scale = running ? 0.62 : 1.0;
        var offset = (Size - Size * scale) / 2;
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M20,6 L44,6 L58,24 L32,60 L6,24 Z"),
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#F7CF6B"), 0), new GradientStop(Color.Parse("#E09A26"), 0.55), new GradientStop(Color.Parse("#A9600E"), 1) },
            },
            RenderTransform = new MatrixTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset, offset)),
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute),
        });

        if (running)
        {
            canvas.Children.Add(new Ellipse { Width = 60, Height = 60, Stroke = Track, StrokeThickness = 6, [Canvas.LeftProperty] = 2.0, [Canvas.TopProperty] = 2.0 });
            canvas.Children.Add(Arc(Math.Clamp(state.Percent, 0, 100)));
        }

        if (state.Kind == ActivityKind.Analyzing)
        {
            canvas.Children.Add(Dot(Blue, 44, 44));
        }

        if (state.NeedsAttention)
        {
            canvas.Children.Add(Dot(Red, 44, 0));
        }

        canvas.Measure(new Size(Size, Size));
        canvas.Arrange(new Rect(0, 0, Size, Size));
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size));
        bitmap.Render(canvas);
        return bitmap;
    }

    public static WindowIcon Render(ActivityState state)
    {
        using var bitmap = RenderBitmap(state);
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    private static Control Arc(double percent)
    {
        const double radius = 29;
        const double center = 32;
        if (percent >= 99.9)
        {
            return new Ellipse { Width = 58, Height = 58, Stroke = Amber, StrokeThickness = 6, [Canvas.LeftProperty] = 3.0, [Canvas.TopProperty] = 3.0 };
        }

        var angle = Math.Max(percent, 1) / 100 * 2 * Math.PI;
        var x = center + radius * Math.Sin(angle);
        var y = center - radius * Math.Cos(angle);
        var large = percent > 50 ? 1 : 0;
        var data = string.Create(CultureInfo.InvariantCulture, $"M{center},{center - radius} A{radius},{radius} 0 {large} 1 {x:0.##},{y:0.##}");
        return new Path { Data = Geometry.Parse(data), Stroke = Amber, StrokeThickness = 6, StrokeLineCap = PenLineCap.Round };
    }

    private static Ellipse Dot(IBrush brush, double left, double top) => new()
    {
        Width = 20,
        Height = 20,
        Fill = brush,
        Stroke = Brushes.White,
        StrokeThickness = 2,
        [Canvas.LeftProperty] = left,
        [Canvas.TopProperty] = top,
    };
}
