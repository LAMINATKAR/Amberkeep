namespace Amberkeep.Core.FileSystem;

/// <summary>
/// Minimal read-only file system access used by scanning and comparison.
/// Exists mainly so tests can simulate failures; production code uses <see cref="PhysicalFileSystem"/>.
/// </summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);

    bool FileExists(string path);

    /// <summary>File system format of the volume holding <paramref name="path"/> (e.g. "NTFS", "FAT32"); null when unknown.</summary>
    string? GetVolumeFormat(string path);

    /// <summary>Lists the direct children of a directory (including hidden and system entries).</summary>
    /// <exception cref="IOException">The directory cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the directory is denied.</exception>
    IReadOnlyList<FileSystemEntry> ListDirectory(string path);

    /// <summary>Opens a file for sequential reading without blocking other readers or writers.</summary>
    Stream OpenRead(string path);
}
