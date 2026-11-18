namespace Amberkeep.App.Services;

/// <summary>Placeholder until the window provides real dialogs: every question is answered with "no".</summary>
public sealed class NoDialogs : IDialogService
{
    public static NoDialogs Instance { get; } = new();

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task<string?> PickSaveFileAsync(string title, string suggestedFileName) => Task.FromResult<string?>(null);

    public Task<string?> PickOpenFileAsync(string title) => Task.FromResult<string?>(null);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool destructive = false) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<bool>?> ConfirmWithOptionsAsync(
        string title, string message, string confirmText, IReadOnlyList<DialogOption> options, bool destructive = false) =>
        Task.FromResult<IReadOnlyList<bool>?>(null);

    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

    public void OpenFolder(string path)
    {
    }

    public void RevealInFileManager(string path)
    {
    }

    public void OpenFile(string path)
    {
    }
}
