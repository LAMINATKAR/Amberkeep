using System.Reflection;
using Amberkeep.App.Services;
using Amberkeep.Storage.Config;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberkeep.App.ViewModels;

public sealed record LicenseInfo(string Component, string License, string Url);

/// <summary>Application settings, export and import of backups, clean-up and About (SPEC 6.7, 6.9).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IShell _shell;
    private bool _loading = true;

    public SettingsViewModel(IShell shell)
    {
        _shell = shell;
        _historyLimit = shell.Services.Config.Settings.HistoryLimit;
        _loading = false;
    }

    public bool CanEdit => !_shell.Services.IsReadOnly;

    public string DataFolder => _shell.Services.Store.BaseDirectory;

    public string Version { get; } =
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(SettingsViewModel).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public IReadOnlyList<LicenseInfo> Licenses { get; } =
    [
        new(".NET runtime and libraries", "MIT", "https://github.com/dotnet/runtime"),
        new("Avalonia UI", "MIT", "https://github.com/AvaloniaUI/Avalonia"),
        new("CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new("Inter typeface", "SIL Open Font License 1.1", "https://github.com/rsms/inter"),
    ];

    [ObservableProperty]
    private decimal? _historyLimit;

    [ObservableProperty]
    private string? _status;

    partial void OnHistoryLimitChanged(decimal? value)
    {
        if (_loading || value is null || !CanEdit)
        {
            return;
        }

        var limit = (int)Math.Clamp(value.Value, 1, 10_000);
        try
        {
            var config = _shell.Services.Config;
            _shell.Services.Save(config with { Settings = config.Settings with { HistoryLimit = limit } });
            Status = $"History keeps the last {limit:N0} runs.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = $"Not saved: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Back() => _shell.ShowDashboard();

    [RelayCommand]
    private void OpenDataFolder() => _shell.Dialogs.OpenFolder(DataFolder);

    [RelayCommand]
    private async Task ExportAsync()
    {
        var jobs = _shell.Services.Config.Jobs;
        if (jobs.Count == 0)
        {
            await _shell.Dialogs.ShowMessageAsync("Nothing to export", "There are no backups yet.");
            return;
        }

        var answers = await _shell.Dialogs.ConfirmWithOptionsAsync(
            "Export backups",
            "The selected backups are saved to a file that can be imported on another computer. Disks are recognized there by their markers.",
            "Export…",
            jobs.Select(j => new DialogOption(j.Name, true)).ToList());
        if (answers is null)
        {
            return;
        }

        var selected = jobs.Where((_, i) => answers[i]).Select(j => j.Id).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var path = await _shell.Dialogs.PickSaveFileAsync("Export backups", "amberkeep-backups.json");
        if (path is null)
        {
            return;
        }

        try
        {
            ConfigTransfer.Export(_shell.Services.FileSystem, _shell.Services.Config, selected, path);
            Status = $"{Format.Count(selected.Count, "backup", "backups")} exported to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _shell.Dialogs.ShowMessageAsync("Export failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = await _shell.Dialogs.PickOpenFileAsync("Import backups");
        if (path is null)
        {
            return;
        }

        try
        {
            var imported = ConfigTransfer.ReadImport(_shell.Services.FileSystem, path);
            var existing = imported.Count(j => _shell.Services.Config.FindJob(j.Id) is not null);
            var replace = existing > 0 && await _shell.Dialogs.ConfirmAsync(
                "Replace existing backups?",
                $"{Format.Count(existing, "backup in the file already exists", "backups in the file already exist")} here. Replace them with the imported version?",
                "Replace");

            var result = ConfigTransfer.Merge(_shell.Services.Config, imported, replace);
            if (result.Added.Count + result.Replaced.Count > 0)
            {
                _shell.Services.Save(result.Config);
            }

            var lines = new List<string>();
            if (result.Added.Count > 0)
            {
                lines.Add("Added: " + string.Join(", ", result.Added));
            }

            if (result.Replaced.Count > 0)
            {
                lines.Add("Replaced: " + string.Join(", ", result.Replaced));
            }

            lines.AddRange(result.Skipped.Select(s => $"Skipped '{s.Name}': {s.Reason}"));
            await _shell.Dialogs.ShowMessageAsync("Import finished", lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "The file contains no backups.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await _shell.Dialogs.ShowMessageAsync("Import failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CleanUpEverythingAsync()
    {
        if (!CanEdit)
        {
            // Settings from a newer version (or a read-only folder) are not ours to delete.
            await _shell.Dialogs.ShowMessageAsync("Not available", _shell.Services.StartupMessage ?? "Settings are read-only.");
            return;
        }

        var answers = await _shell.Dialogs.ConfirmWithOptionsAsync(
            "Remove everything Amberkeep created?",
            "Settings, backups and history next to the program are deleted and Amberkeep closes. Backed-up files on your disks stay where they are. Afterwards you can simply delete the program file.",
            "Remove and close",
            [new DialogOption("Also remove markers from connected backup disks"), new DialogOption("Also delete archives on connected backup disks")],
            destructive: true);
        if (answers is null)
        {
            return;
        }

        var report = _shell.Services.Cleanup.CleanUpEverything(_shell.Services.Config, answers[0], answers[0] && answers[1]);
        var lines = report.NotCleaned.Concat(report.Errors).ToList();
        if (lines.Count > 0)
        {
            await _shell.Dialogs.ShowMessageAsync("Some files could not be removed", string.Join(Environment.NewLine, lines));
        }

        _shell.RequestExit();
    }
}
