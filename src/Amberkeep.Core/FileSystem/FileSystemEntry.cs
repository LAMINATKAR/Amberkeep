namespace Amberkeep.Core.FileSystem;

/// <summary>Kind of a file system entry.</summary>
public enum EntryKind
{
    File,
    Directory,
}

/// <summary>One entry of a directory listing.</summary>
/// <param name="Name">File or directory name (no path).</param>
/// <param name="Kind">File or directory.</param>
/// <param name="Length">Size in bytes; 0 for directories.</param>
/// <param name="LastWriteTimeUtc">Last modification time in UTC.</param>
/// <param name="IsLink">True for symbolic links and junctions. Links are never followed.</param>
public sealed record FileSystemEntry(
    string Name,
    EntryKind Kind,
    long Length,
    DateTime LastWriteTimeUtc,
    bool IsLink);
