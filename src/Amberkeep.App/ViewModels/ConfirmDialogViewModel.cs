using System.Collections.ObjectModel;
using Amberkeep.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberkeep.App.ViewModels;

public sealed partial class DialogOptionViewModel(string text, bool isChecked) : ObservableObject
{
    public string Text { get; } = text;

    [ObservableProperty]
    private bool _isChecked = isChecked;
}

/// <summary>An in-window modal dialog: message, optional checkboxes, confirm and (optionally) cancel.</summary>
public sealed partial class ConfirmDialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfirmDialogViewModel(
        string title,
        string message,
        string confirmText,
        string? cancelText,
        bool destructive,
        IEnumerable<DialogOption>? options = null)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;
        IsDestructive = destructive;
        Options = new ObservableCollection<DialogOptionViewModel>(
            (options ?? []).Select(o => new DialogOptionViewModel(o.Text, o.IsChecked)));
    }

    public string Title { get; }

    public string Message { get; }

    public string ConfirmText { get; }

    public string? CancelText { get; }

    public bool HasCancel => CancelText is not null;

    public bool IsDestructive { get; }

    public ObservableCollection<DialogOptionViewModel> Options { get; }

    public bool HasOptions => Options.Count > 0;

    public Task<bool> Result => _result.Task;

    [RelayCommand]
    private void Confirm() => _result.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => _result.TrySetResult(false);
}
