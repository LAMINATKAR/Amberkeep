using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.History;
using Amberkeep.Storage.Targets;

namespace Amberkeep.Storage.Cleanup;

/// <summary>Removes the system autostart entry. Implemented in phase 6.</summary>
public interface IAutostart
{
    void Remove();
}

public sealed class NoAutostart : IAutostart
{
    public static NoAutostart Instance { get; } = new();

    public void Remove()
    {
    }
}

/// <summary>What a clean-up removed, and where program files had to stay.</summary>
public sealed class CleanupReport
{
    public List<string> Removed { get; } = [];

    /// <summary>Targets that were not connected; their <c>.amberkeep</c> folder is still there.</summary>
    public List<string> NotCleaned { get; } = [];

    public List<string> Errors { get; } = [];
}

/// <summary>
/// Removes Amberkeep's own files (SPEC 4.8). Backed-up data is never deleted; archives only when asked.
/// </summary>
public sealed class CleanupService(
    AppStore store,
    HistoryStore history,
    IWritableFileSystem fileSystem,
    IVolumeProvider volumes,
    IAutostart autostart)
{
    private readonly TargetLocator _locator = new(fileSystem, volumes);

    /// <summary>Removes a target from its job and saves the configuration.</summary>
    public (AppConfig Config, CleanupReport Report) RemoveTarget(
        AppConfig config, Guid jobId, Guid targetId, bool removeMarker, bool removeArchive)
    {
        ArgumentNullException.ThrowIfNull(config);
        var report = new CleanupReport();
        var job = config.FindJob(jobId) ?? throw new InvalidOperationException("The backup does not exist.");
        var target = job.FindTarget(targetId) ?? throw new InvalidOperationException("The target does not exist.");

        CleanTarget(job, target, removeMarker, removeArchive, report);
        var updated = config.WithJob(job.WithoutTarget(targetId));
        store.Save(updated);
        return (updated, report);
    }

    /// <summary>Removes a job with its history and saves the configuration.</summary>
    public (AppConfig Config, CleanupReport Report) DeleteJob(
        AppConfig config, Guid jobId, bool cleanTargets, bool removeArchives)
    {
        ArgumentNullException.ThrowIfNull(config);
        var report = new CleanupReport();
        var job = config.FindJob(jobId) ?? throw new InvalidOperationException("The backup does not exist.");

        foreach (var target in job.Targets)
        {
            CleanTarget(job, target, cleanTargets, cleanTargets && removeArchives, report);
        }

        try
        {
            var deleted = history.DeleteForJob(jobId);
            report.Removed.Add($"{deleted} history records of '{job.Name}'");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Errors.Add($"History of '{job.Name}' could not be deleted: {ex.Message}");
        }

        var updated = config.WithoutJob(jobId);
        store.Save(updated);
        return (updated, report);
    }

    /// <summary>
    /// Removes everything Amberkeep created: autostart, (optionally) markers and archives on connected targets,
    /// and all files next to the executable except the executable itself.
    /// </summary>
    public CleanupReport CleanUpEverything(AppConfig config, bool cleanTargets, bool removeArchives)
    {
        ArgumentNullException.ThrowIfNull(config);
        var report = new CleanupReport();

        Try(report, "Autostart entry", autostart.Remove);

        if (cleanTargets)
        {
            foreach (var job in config.Jobs)
            {
                foreach (var target in job.Targets)
                {
                    CleanTarget(job, target, removeMarker: true, removeArchives, report);
                }
            }
        }

        // amberkeep.json plus set-aside damaged copies (amberkeep.json.bad-*), interrupted saves
        // (amberkeep.json.*.tmp) and write-test leftovers. Nothing else next to the exe is ours.
        if (fileSystem.DirectoryExists(store.BaseDirectory))
        {
            foreach (var entry in fileSystem.ListDirectory(store.BaseDirectory))
            {
                if (entry.Kind == EntryKind.File
                    && (entry.Name.StartsWith(AppStore.ConfigFileName, StringComparison.OrdinalIgnoreCase)
                        || entry.Name.StartsWith(AppStore.WriteProbePrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    Try(report, entry.Name, () => fileSystem.DeleteFile(Path.Join(store.BaseDirectory, entry.Name)));
                }
            }
        }

        DeleteFolder(store.HistoryDirectory, report);
        DeleteFolder(store.StateDirectory, report);
        return report;
    }

    private void CleanTarget(JobConfig job, TargetConfig target, bool removeMarker, bool removeArchive, CleanupReport report)
    {
        if (!removeMarker && !removeArchive)
        {
            return;
        }

        var label = $"'{job.Name}' → '{target.Name}'";
        var status = _locator.Locate(target);
        if (status.State == TargetState.Unverified)
        {
            report.NotCleaned.Add($"{label}: the folder {status.Path} could not be verified ({status.Reason}). Nothing was removed there.");
            return;
        }

        if (!status.IsConnected)
        {
            report.NotCleaned.Add($"{label}: not connected ({target.LastKnownPath}). Its .amberkeep folder stays there.");
            return;
        }

        var programFolder = MarkerFile.ProgramFolder(status.Path!);
        if (removeArchive)
        {
            DeleteFolder(Path.Join(programFolder, "archive"), report);
        }

        if (removeMarker)
        {
            Try(report, $"Marker of {label}", () =>
            {
                // Connected means the marker belongs to this target; check again right before deleting.
                if (MarkerFile.BelongsTo(fileSystem, status.Path!, target.Id))
                {
                    fileSystem.DeleteFile(MarkerFile.PathOf(status.Path!));
                }
            });
        }

        Try(report, $"Empty .amberkeep of {label}", () =>
        {
            if (fileSystem.DirectoryExists(programFolder) && fileSystem.IsDirectoryEmpty(programFolder))
            {
                fileSystem.DeleteEmptyDirectory(programFolder);
            }
        });
    }

    private void DeleteFolder(string path, CleanupReport report) => Try(report, path, () =>
    {
        if (fileSystem.DirectoryExists(path))
        {
            fileSystem.DeleteDirectoryTree(path);
        }
    });

    private static void Try(CleanupReport report, string what, Action action)
    {
        try
        {
            action();
            report.Removed.Add(what);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Errors.Add($"{what}: {ex.Message}");
        }
    }
}
