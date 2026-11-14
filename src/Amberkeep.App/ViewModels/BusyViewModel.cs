using Amberkeep.Core.Diff;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberkeep.App.ViewModels;

/// <summary>Shown while a source and target are being compared.</summary>
public sealed partial class BusyViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cancellation = new();

    public BusyViewModel(string title, string subtitle)
    {
        Title = title;
        Subtitle = subtitle;

        // Created here, on the UI thread, so that reports are marshalled back to it.
        Progress = new Progress<AnalysisProgress>(p => Status = p.Stage switch
        {
            AnalysisStage.Scanning => $"Scanning…  {p.SourceEntries:N0} items in the source, {p.TargetEntries:N0} on the target",
            AnalysisStage.Comparing => $"Comparing contents…  {p.ComparedFiles:N0} of {p.FilesToCompare:N0} files",
            _ => "Done",
        });
    }

    public string Title { get; }

    public string Subtitle { get; }

    public IProgress<AnalysisProgress> Progress { get; }

    public CancellationToken Token => _cancellation.Token;

    [ObservableProperty]
    private string _status = "Looking for the target…";

    [RelayCommand]
    private Task CancelAsync() => _cancellation.CancelAsync();
}
