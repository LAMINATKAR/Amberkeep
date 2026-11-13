using Amberkeep.Core.Paths;

namespace Amberkeep.Storage.Targets;

public static class TargetPaths
{
    /// <summary>
    /// Splits a full path into the root of the volume it lives on and the path below that root,
    /// e.g. <c>E:\Backup\Photos</c> → (<c>E:\</c>, <c>Backup\Photos</c>) or <c>/media/usb/Backup</c> → (<c>/media/usb/</c>, <c>Backup</c>).
    /// </summary>
    /// <param name="fullPath">Absolute path.</param>
    /// <param name="volumeRoots">Mounted volume roots; the longest one containing the path wins.</param>
    public static (string Root, string RelativePath) Split(string fullPath, IReadOnlyList<string> volumeRoots)
    {
        ArgumentNullException.ThrowIfNull(volumeRoots);
        var full = Path.GetFullPath(fullPath);
        var comparison = CaseSensitivity.Auto.IsCaseSensitive() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var withSeparator = WithSeparator(full);

        var root = volumeRoots
            .Select(WithSeparator)
            .Where(r => withSeparator.StartsWith(r, comparison))
            .MaxBy(r => r.Length)
            ?? WithSeparator(Path.GetPathRoot(full) ?? string.Empty);

        var relative = full.Length > root.Length ? full[root.Length..] : string.Empty;
        return (root, Path.TrimEndingDirectorySeparator(relative));
    }

    private static string WithSeparator(string path) =>
        path.Length == 0 || Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
