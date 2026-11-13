namespace Amberkeep.Core.FileSystem;

/// <summary>
/// File system access needed to execute a mirror on the target.
/// Callers must only pass target paths; see <c>Execution.WriteGuard</c>.
/// </summary>
public interface IWritableFileSystem : IFileSystem
{
    /// <summary>Current state of a single path, or null when nothing exists there.</summary>
    FileSystemEntry? GetEntry(string path);

    bool IsDirectoryEmpty(string path);

    /// <summary>Creates the directory and any missing parents. No-op when it exists.</summary>
    void CreateDirectory(string path);

    /// <summary>Creates a new file for writing; fails when the file exists.</summary>
    Stream CreateFile(string path);

    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    void MoveDirectory(string sourcePath, string destinationPath);

    void DeleteFile(string path);

    /// <exception cref="IOException">The directory is not empty.</exception>
    void DeleteEmptyDirectory(string path);

    /// <summary>Deletes a directory with all its content, including read-only files.</summary>
    void DeleteDirectoryTree(string path);

    FileAttributes GetAttributes(string path);

    void SetAttributes(string path, FileAttributes attributes);

    void SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc);

    /// <summary>
    /// Copies modification time, creation time (where supported) and the ReadOnly/Hidden/System/Archive attributes
    /// from <paramref name="sourcePath"/> (read only) to <paramref name="destinationPath"/>.
    /// </summary>
    void CopyMetadata(string sourcePath, string destinationPath);

    /// <summary>Free space available to the user on the volume holding <paramref name="existingPath"/>; null when unknown.</summary>
    long? GetAvailableFreeSpace(string existingPath);
}
