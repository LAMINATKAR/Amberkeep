using Amberkeep.App.ViewModels;
using Amberkeep.App.Views;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Amberkeep.App;

/// <summary>Maps page view models to their views (explicitly, so it stays trim-safe).</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        DashboardViewModel => new DashboardView(),
        JobEditorViewModel => new JobEditorView(),
        DiffViewModel => new DiffView(),
        RunViewModel => new RunView(),
        BusyViewModel => new BusyView(),
        OneTimeRunViewModel => new OneTimeRunView(),
        ConfirmDialogViewModel => new ConfirmDialogView(),
        HistoryViewModel => new HistoryView(),
        ArchiveViewModel => new ArchiveView(),
        SettingsViewModel => new SettingsView(),
        _ => new TextBlock { Text = $"No view for {param?.GetType().Name}" },
    };

    public bool Match(object? data) =>
        data is DashboardViewModel or JobEditorViewModel or DiffViewModel or RunViewModel
            or BusyViewModel or OneTimeRunViewModel or ConfirmDialogViewModel
            or HistoryViewModel or ArchiveViewModel or SettingsViewModel;
}
