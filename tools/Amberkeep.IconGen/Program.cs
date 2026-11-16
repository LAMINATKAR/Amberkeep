// Development tool: renders the logo (LogoArt) into the application icon.
// Usage: dotnet run --project tools/Amberkeep.IconGen
// Writes src/Amberkeep.App/Assets/amberkeep.ico and docs/images/logo.png (both committed) and sandbox/icon-preview.png.
// Run it after changing the logo.
using Amberkeep.App;
using Amberkeep.App.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var images = sizes.Select(size =>
{
    using var bitmap = LogoArt.Render(size);
    using var png = new MemoryStream();
    bitmap.Save(png, new PngBitmapEncoderOptions());
    return (size, png.ToArray());
}).ToList();

var icon = Path.GetFullPath(Path.Combine("src", "Amberkeep.App", "Assets", "amberkeep.ico"));
Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
using (var file = File.Create(icon))
{
    IcoWriter.Write(file, images);
}

Console.WriteLine($"{icon}  ({images.Count} sizes, {new FileInfo(icon).Length:N0} bytes)");

// The logo for the README.
var logo = Path.GetFullPath(Path.Combine("docs", "images", "logo.png"));
Directory.CreateDirectory(Path.GetDirectoryName(logo)!);
File.WriteAllBytes(logo, images.Single(i => i.size == 256).Item2);
Console.WriteLine(logo);

// A preview strip: every size side by side, plus each one magnified to 64 px to judge the small ones.
var strip = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
foreach (var (size, png) in images)
{
    strip.Children.Add(new Avalonia.Controls.Image { Source = new Bitmap(new MemoryStream(png)), Width = size, Height = size, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom });
}

foreach (var (size, png) in images.Where(i => i.size <= 32))
{
    var image = new Avalonia.Controls.Image { Source = new Bitmap(new MemoryStream(png)), Width = 64, Height = 64, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
    Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
    strip.Children.Add(image);
}

var preview = new Border { Background = Avalonia.Media.Brushes.White, Child = strip };
var width = images.Sum(i => i.size) + 4 * 64 + 12 * (images.Count + 4) + 12;
const int height = 280;
preview.Measure(new Size(width, height));
preview.Arrange(new Rect(0, 0, width, height));
using var render = new RenderTargetBitmap(new PixelSize(width, height));
render.Render(preview);
var previewPath = Path.GetFullPath(Path.Combine("sandbox", "icon-preview.png"));
Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
render.Save(previewPath, new PngBitmapEncoderOptions());
Console.WriteLine(previewPath);
