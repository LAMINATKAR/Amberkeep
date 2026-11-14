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

    void OpenFolder(string path);
}
