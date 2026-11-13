using Amberkeep.Core.FileSystem;

namespace Amberkeep.Tests.Infrastructure;

/// <summary>Real file system that refuses to list selected directories.</summary>
internal sealed class FaultyFileSystem(params string[] unreadableDirectories) : IFileSystem
{
    private readonly HashSet<string> _unreadable = new(
        unreadableDirectories.Select(Normalize), StringComparer.OrdinalIgnoreCase);

    public List<string> ListedDirectories { get; } = [];

    public bool DirectoryExists(string path) => PhysicalFileSystem.Instance.DirectoryExists(path);

    public bool FileExists(string path) => PhysicalFileSystem.Instance.FileExists(path);

    /// <summary>Overrides the reported volume format, e.g. "FAT32".</summary>
    public string? VolumeFormat { get; init; }

    public string? GetVolumeFormat(string path) => VolumeFormat ?? PhysicalFileSystem.Instance.GetVolumeFormat(path);

    public IReadOnlyList<FileSystemEntry> ListDirectory(string path)
    {
        ListedDirectories.Add(Normalize(path));
        if (_unreadable.Contains(Normalize(path)))
        {
            throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        }

        return PhysicalFileSystem.Instance.ListDirectory(path);
    }

    public Stream OpenRead(string path) => PhysicalFileSystem.Instance.OpenRead(path);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
