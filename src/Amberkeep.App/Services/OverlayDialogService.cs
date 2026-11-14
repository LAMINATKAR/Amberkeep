using System.Diagnostics;
using Amberkeep.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Amberkeep.App.Services;

/// <summary>Dialogs shown inside the main window; folder and file pickers from the OS.</summary>
internal sealed class OverlayDialogService(MainViewModel host, Func<TopLevel?> topLevel) : IDialogService
{
    public async Task<string?> PickFolderAsync(string title)
    {
        if (topLevel()?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedFileName)
    {
        if (topLevel()?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = Path.GetExtension(suggestedFileName),
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync(string title)
    {
        if (topLevel()?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool destructive = false) =>
        host.ShowDialogAsync(new ConfirmDialogViewModel(title, message, confirmText, "Cancel", destructive));

    public async Task<IReadOnlyList<bool>?> ConfirmWithOptionsAsync(
        string title, string message, string confirmText, IReadOnlyList<DialogOption> options, bool destructive = false)
    {
        var dialog = new ConfirmDialogViewModel(title, message, confirmText, "Cancel", destructive, options);
        return await host.ShowDialogAsync(dialog) ? dialog.Options.Select(o => o.IsChecked).ToList() : null;
    }

    public Task ShowMessageAsync(string title, string message) =>
        host.ShowDialogAsync(new ConfirmDialogViewModel(title, message, "OK", null, destructive: false));

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", $"\"{path}\"") { UseShellExecute = false });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No file manager available; nothing else to do.
        }
    }
}
