using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.History;
using Amberkeep.Storage.Targets;

namespace Amberkeep.Storage.Cleanup;

/// <summary>The optional "start with the system" entry (the only thing Amberkeep writes outside its folder).</summary>
public interface IAutostart
{
    bool IsEnabled { get; }

    /// <summary>Creates or updates the entry so it starts the current executable in the background.</summary>
    void Enable();

    void Remove();
}

public sealed class NoAutostart : IAutostart
{
    public static NoAutostart Instance { get; } = new();

    public bool IsEnabled => false;

    public void Enable()
    {
    }

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

    /// <summary>Removes a target from its job and saves the configuration; the target's files are cleaned afterwards.</summary>
    public (AppConfig Config, CleanupReport Report) RemoveTarget(
        AppConfig config, Guid jobId, Guid targetId, bool removeMarker, bool removeArchive)
    {
        ArgumentNullException.ThrowIfNull(config);
        var report = new CleanupReport();
        var job = config.FindJob(jobId) ?? throw new InvalidOperationException("The backup does not exist.");
        var target = job.FindTarget(targetId) ?? throw new InvalidOperationException("The target does not exist.");

        // Saved first: if saving fails, nothing on the target has been removed yet.
        var updated = config.WithJob(job.WithoutTarget(targetId));
        store.Save(updated);
        CleanTarget(job, target, removeMarker, removeArchive, report);
        return (updated, report);
    }

    /// <summary>Removes a job and saves the configuration; then its history and (optionally) target files are cleaned.</summary>
    public (AppConfig Config, CleanupReport Report) DeleteJob(
        AppConfig config, Guid jobId, bool cleanTargets, bool removeArchives)
    {
        ArgumentNullException.ThrowIfNull(config);
        var job = config.FindJob(jobId) ?? throw new InvalidOperationException("The backup does not exist.");

        // Saved first: if saving fails, nothing has been removed yet.
        var updated = config.WithoutJob(jobId);
        store.Save(updated);
        return (updated, CleanUpRemovedJob(job, cleanTargets, removeArchives));
    }

    /// <summary>
    /// Removes the history and (optionally) the markers and archives of a job that is no longer in the saved
    /// configuration. Callers save the configuration first.
    /// </summary>
    public CleanupReport CleanUpRemovedJob(JobConfig job, bool cleanTargets, bool removeArchives)
    {
        ArgumentNullException.ThrowIfNull(job);
        var report = new CleanupReport();
        foreach (var target in job.Targets)
        {
            CleanTarget(job, target, cleanTargets, cleanTargets && removeArchives, report);
        }

        try
        {
            var deleted = history.DeleteForJob(job.Id);
            report.Removed.Add($"{deleted} history records of '{job.Name}'");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Errors.Add($"History of '{job.Name}' could not be deleted: {ex.Message}");
        }

        return report;
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

        // Only files Amberkeep wrote; anything else in the program folder belongs to the user.
        if (fileSystem.DirectoryExists(store.BaseDirectory))
        {
            foreach (var entry in fileSystem.ListDirectory(store.BaseDirectory))
            {
                if (entry.Kind == EntryKind.File && AppStore.IsOwnFile(entry.Name))
                {
                    Try(report, entry.Name, () => fileSystem.DeleteFile(Path.Join(store.BaseDirectory, entry.Name)));
                }
            }
        }

        // history/: only our record files; the folder goes only when nothing else is in it.
        // state/ is not used before two-way sync, so whatever is there is not ours.
        try
        {
            var problems = history.DeleteOwnFiles();
            report.Errors.AddRange(problems);
            if (problems.Count == 0)
            {
                report.Removed.Add(store.HistoryDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Errors.Add($"{store.HistoryDirectory}: {ex.Message}");
        }

        return report;
    }

    /// <summary>
    /// Removes the marker and/or archive of one target folder without changing the configuration
    /// (for callers that save the configuration themselves, e.g. the job editor).
    /// </summary>
    public CleanupReport CleanTargetFolder(JobConfig job, TargetConfig target, bool removeMarker, bool removeArchive)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(target);
        var report = new CleanupReport();
        CleanTarget(job, target, removeMarker, removeArchive, report);
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
