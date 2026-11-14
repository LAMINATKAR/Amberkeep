using Amberkeep.App.Services;
using Amberkeep.App.ViewModels;
using Amberkeep.App.Views;
using Amberkeep.Storage;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Amberkeep.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.Create(AppStore.ExecutableDirectory);
            var main = new MainViewModel(services);
            var window = new MainWindow { DataContext = main };
            main.Dialogs = new OverlayDialogService(main, () => window);
            main.ExitRequested += (_, _) => desktop.Shutdown();
            main.Start();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
