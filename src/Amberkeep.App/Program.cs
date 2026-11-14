using Amberkeep.App.Platform;
using Amberkeep.Storage;
using Avalonia;

namespace Amberkeep.App;

internal static class Program
{
    /// <summary>Set when this process is the first Amberkeep in its folder.</summary>
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        using var instance = SingleInstance.Acquire(AppStore.ExecutableDirectory);
        if (!instance.IsFirst)
        {
            // Amberkeep already runs from this folder (maybe only in the tray): show its window instead.
            instance.SignalFirst(TimeSpan.FromSeconds(3));
            return 0;
        }

        Instance = instance;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by the visual designer, tests and the screenshot tool.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
