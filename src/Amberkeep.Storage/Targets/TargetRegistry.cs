using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;

namespace Amberkeep.Storage.Targets;

/// <param name="Target">The updated target; null when the user must confirm first.</param>
/// <param name="ConfirmationNeeded">Why the folder cannot be linked without confirmation.</param>
public sealed record RelinkResult(TargetConfig? Target, string? ConfirmationNeeded);

/// <summary>Creates targets and links them to folders by writing their marker.</summary>
public sealed class TargetRegistry(IWritableFileSystem fileSystem, IVolumeProvider volumes, TimeProvider timeProvider)
{
    public TargetRegistry()
        : this(PhysicalFileSystem.Instance, PhysicalVolumeProvider.Instance, TimeProvider.System)
    {
    }

    /// <summary>Creates a new target in <paramref name="folder"/> (created when missing) and writes its marker.</summary>
    /// <param name="config">All jobs; the folder must not overlap any source or target.</param>
    /// <param name="job">Job the target belongs to (may not be saved in <paramref name="config"/> yet).</param>
    /// <param name="folder">Target folder.</param>
    /// <param name="name">Display name, e.g. "External HDD".</param>
    /// <param name="replaceForeignMarker">Overwrite a marker of another target found in the folder.</param>
    /// <exception cref="InvalidOperationException">The folder overlaps a source or target, or is used by another target.</exception>
    public TargetConfig AddTarget(AppConfig config, JobConfig job, string folder, string name, bool replaceForeignMarker = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var full = Path.GetFullPath(folder);
        if (config.FindPathConflict(full, pendingJob: job) is { } conflict)
        {
            throw new InvalidOperationException(conflict);
        }

        if (fileSystem.FileExists(full))
        {
            throw new InvalidOperationException($"The target path is a file, not a folder: {full}");
        }

        var (state, existing) = MarkerFile.Read(fileSystem, full);
        if (state == MarkerState.Present && !replaceForeignMarker)
        {
            throw new InvalidOperationException(
                $"This folder is already a target of the backup '{existing!.JobName}'. Confirm to take it over.");
        }

        var target = new TargetConfig
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(full) : name.Trim(),
            CreatedUtc = timeProvider.GetUtcNow(),
        };
        target = WithLocation(target, full);

        fileSystem.CreateDirectory(full);
        MarkerFile.Write(fileSystem, full, NewMarker(job, target));
        return target;
    }

    /// <summary>Links an existing target to a folder the user picked ("Locate target…").</summary>
    /// <param name="config">All jobs; the folder must not overlap any other source or target.</param>
    /// <param name="job">Job the target belongs to.</param>
    /// <param name="target">Target to link.</param>
    /// <param name="folder">Folder the user picked.</param>
    /// <param name="confirmed">The user confirmed linking a folder whose marker is missing or foreign.</param>
    public RelinkResult Relink(AppConfig config, JobConfig job, TargetConfig target, string folder, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(target);

        var full = Path.GetFullPath(folder);
        if (!fileSystem.DirectoryExists(full))
        {
            throw new InvalidOperationException($"The folder does not exist: {full}");
        }

        if (config.FindPathConflict(full, target.Id, job) is { } conflict)
        {
            throw new InvalidOperationException(conflict);
        }

        var (state, marker) = MarkerFile.Read(fileSystem, full);
        if (state == MarkerState.Present && marker!.TargetId == target.Id)
        {
            return new RelinkResult(WithLocation(target, full), null);
        }

        if (!confirmed)
        {
            var reason = state switch
            {
                MarkerState.Missing => "The folder has no Amberkeep marker. Use it as this target anyway?",
                MarkerState.Unreadable => "The Amberkeep marker in this folder cannot be read. Replace it?",
                _ => $"The folder belongs to the backup '{marker!.JobName}'. Take it over?",
            };
            return new RelinkResult(null, reason);
        }

        MarkerFile.Write(fileSystem, full, NewMarker(job, target));
        return new RelinkResult(WithLocation(target, full), null);
    }

    /// <summary>The marker found in a folder, if any (to ask the user before taking a folder over).</summary>
    public (MarkerState State, string? JobName) ReadMarker(string folder)
    {
        var (state, marker) = MarkerFile.Read(fileSystem, Path.GetFullPath(folder));
        return (state, marker?.JobName);
    }

    /// <summary>Records a new location of the target (after it was found elsewhere or relinked).</summary>
    public TargetConfig WithLocation(TargetConfig target, string fullPath) => target with
    {
        LastKnownPath = fullPath,
        RelativePath = TargetPaths.Split(fullPath, volumes.GetVolumeRoots()).RelativePath,
    };

    private TargetMarker NewMarker(JobConfig job, TargetConfig target) => new()
    {
        TargetId = target.Id,
        JobId = job.Id,
        JobName = job.Name,
        CreatedUtc = timeProvider.GetUtcNow(),
    };
}
