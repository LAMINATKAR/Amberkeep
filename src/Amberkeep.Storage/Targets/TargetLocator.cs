using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Paths;
using Amberkeep.Storage.Config;

namespace Amberkeep.Storage.Targets;

public enum TargetState
{
    /// <summary>Found and verified by its marker. Safe to write.</summary>
    Connected,

    /// <summary>Not found on any mounted volume.</summary>
    NotConnected,

    /// <summary>A folder exists at the last known path, but its marker is missing or belongs to something else. Never written to.</summary>
    Unverified,
}

public sealed record TargetStatus(TargetState State, string? Path, string? Reason)
{
    public static TargetStatus NotConnected { get; } = new(TargetState.NotConnected, null, null);

    public bool IsConnected => State == TargetState.Connected;

    public static TargetStatus Connected(string path) => new(TargetState.Connected, path, null);

    public static TargetStatus Unverified(string path, string reason) => new(TargetState.Unverified, path, reason);
}

/// <summary>Finds a target by its marker, also when the drive letter or mount point changed (SPEC 4.9).</summary>
public sealed class TargetLocator(IFileSystem fileSystem, IVolumeProvider volumes)
{
    public TargetStatus Locate(TargetConfig target)
    {
        ArgumentNullException.ThrowIfNull(target);

        string? unverifiedReason = null;
        if (target.LastKnownPath.Length > 0 && fileSystem.DirectoryExists(target.LastKnownPath))
        {
            var (state, marker) = MarkerFile.Read(fileSystem, target.LastKnownPath);
            if (state == MarkerState.Present && marker!.TargetId == target.Id)
            {
                return TargetStatus.Connected(target.LastKnownPath);
            }

            unverifiedReason = state switch
            {
                MarkerState.Missing => "The folder has no Amberkeep marker – it may be a different disk.",
                MarkerState.Unreadable => "The Amberkeep marker in this folder cannot be read.",
                _ => $"The folder belongs to another backup ('{marker!.JobName}').",
            };
        }

        // Also for a target at the root of a disk (empty relative path): the marker is then in the root itself.
        var comparison = CaseSensitivity.Auto.IsCaseSensitive() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var root in volumes.GetVolumeRoots())
        {
            var candidate = Path.Join(root, target.RelativePath);
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(candidate),
                    Path.TrimEndingDirectorySeparator(target.LastKnownPath),
                    comparison))
            {
                continue;
            }

            if (fileSystem.DirectoryExists(candidate) && MarkerFile.BelongsTo(fileSystem, candidate, target.Id))
            {
                return TargetStatus.Connected(candidate);
            }
        }

        return unverifiedReason is null
            ? TargetStatus.NotConnected
            : TargetStatus.Unverified(target.LastKnownPath, unverifiedReason);
    }
}
