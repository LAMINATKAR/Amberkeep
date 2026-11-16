namespace Amberkeep.App.Services;

/// <summary>A checkbox shown in a confirmation dialog.</summary>
public sealed record DialogOption(string Text, bool IsChecked = false);

/// <summary>User interaction that view models need but cannot do themselves. Replaced by a fake in tests.</summary>
public interface IDialogService
{
    Task<string?> PickFolderAsync(string title);

    Task<string?> PickSaveFileAsync(string title, string suggestedFileName);

    Task<string?> PickOpenFileAsync(string title);

    /// <returns>True when the user confirmed.</returns>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool destructive = false);

    /// <returns>The state of each option when the user confirmed; null when cancelled.</returns>
    Task<IReadOnlyList<bool>?> ConfirmWithOptionsAsync(
        string title, string message, string confirmText, IReadOnlyList<DialogOption> options, bool destructive = false);

    Task ShowMessageAsync(string title, string message);

    /// <summary>A long, preformatted text (e.g. license texts) in a scrollable dialog.</summary>
    Task ShowTextAsync(string title, string text) => ShowMessageAsync(title, text);

    void OpenFolder(string path);

    /// <summary>
    /// Shows a file or folder in the system file manager with the item selected. When it no longer exists,
    /// opens its nearest existing parent folder instead.
    /// </summary>
    void RevealInFileManager(string path);
}
