using Amberkeep.Core.FileSystem;

namespace Amberkeep.Tests.Infrastructure;

/// <summary>Real file system that refuses to list selected directories, or lists extra (made-up) entries.</summary>
internal sealed class FaultyFileSystem(params string[] unreadableDirectories) : IFileSystem
{
    private readonly HashSet<string> _unreadable = new(
        unreadableDirectories.Select(Normalize), StringComparer.OrdinalIgnoreCase);

    private readonly Lock _lock = new();
    private readonly List<string> _listed = [];

    /// <summary>Directories listed so far (source and target are scanned in parallel, so this is guarded).</summary>
    public IReadOnlyList<string> ListedDirectories
    {
        get
        {
            lock (_lock)
            {
                return [.. _listed];
            }
        }
    }

    /// <summary>Entries added to the listing of a directory (full path), e.g. a name that differs only in case.</summary>
    public Dictionary<string, FileSystemEntry[]> ExtraEntries { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool DirectoryExists(string path) => PhysicalFileSystem.Instance.DirectoryExists(path);

    public bool FileExists(string path) => PhysicalFileSystem.Instance.FileExists(path);

    /// <summary>Overrides the reported volume format, e.g. "FAT32".</summary>
    public string? VolumeFormat { get; init; }

    public string? GetVolumeFormat(string path) => VolumeFormat ?? PhysicalFileSystem.Instance.GetVolumeFormat(path);

    public IReadOnlyList<FileSystemEntry> ListDirectory(string path)
    {
        lock (_lock)
        {
            _listed.Add(Normalize(path));
        }

        if (_unreadable.Contains(Normalize(path)))
        {
            throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        }

        var entries = PhysicalFileSystem.Instance.ListDirectory(path);
        return ExtraEntries.TryGetValue(Normalize(path), out var extra) ? [.. entries, .. extra] : entries;
    }

    public Stream OpenRead(string path) => PhysicalFileSystem.Instance.OpenRead(path);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
