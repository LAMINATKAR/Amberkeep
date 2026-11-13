namespace Amberkeep.Core.FileSystem;

/// <summary>Access to the real file system.</summary>
public sealed class PhysicalFileSystem : IWritableFileSystem
{
    private const FileAttributes CopiedAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        // The default skips hidden and system entries; a backup must include them.
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    public static PhysicalFileSystem Instance { get; } = new();

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<FileSystemEntry> ListDirectory(string path)
    {
        var result = new List<FileSystemEntry>();
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", ListOptions))
        {
            result.Add(ToEntry(info));
        }

        return result;
    }

    public FileSystemEntry? GetEntry(string path)
    {
        FileSystemInfo info = new FileInfo(path);
        if (!info.Exists)
        {
            info = new DirectoryInfo(path);
            if (!info.Exists)
            {
                return null;
            }
        }

        return ToEntry(info);
    }

    public bool IsDirectoryEmpty(string path) => !Directory.EnumerateFileSystemEntries(path, "*", ListOptions).Any();

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream CreateFile(string path) => new FileStream(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        Options = FileOptions.SequentialScan,
        BufferSize = 0,
    });

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        WithRetry(() => File.Move(sourcePath, destinationPath, overwrite));

    public void MoveDirectory(string sourcePath, string destinationPath) =>
        WithRetry(() => Directory.Move(sourcePath, destinationPath));

    public void DeleteFile(string path) => WithRetry(() => File.Delete(path));

    /// <summary>
    /// Retries briefly when a file was just written and is still held by another process
    /// (typically an antivirus scan or a search indexer). Genuine problems surface after a few attempts.
    /// </summary>
    private static void WithRetry(Action action)
    {
        int[] delaysMs = [50, 150, 400];
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < delaysMs.Length && IsTransient(ex))
            {
                Thread.Sleep(delaysMs[attempt]);
            }
        }
    }

    // Access denied, sharing violation and lock violation are what a scanner holding the file looks like.
    private static bool IsTransient(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33);

    public void DeleteEmptyDirectory(string path) => Directory.Delete(path, recursive: false);

    public void DeleteDirectoryTree(string path)
    {
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", ListOptions))
        {
            var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            var isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(info);
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
            {
                info.Attributes &= ~FileAttributes.ReadOnly;
            }

            if (isDirectory && !isLink)
            {
                DeleteDirectoryTree(info.FullName);
            }
            else if (isDirectory)
            {
                // Remove the link itself, never what it points to.
                Directory.Delete(info.FullName, recursive: false);
            }
            else
            {
                info.Delete();
            }
        }

        Directory.Delete(path, recursive: false);
    }

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(path, attributes);

    public void SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc) =>
        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);

    public void CopyMetadata(string sourcePath, string destinationPath)
    {
        var source = new FileInfo(sourcePath);
        File.SetLastWriteTimeUtc(destinationPath, source.LastWriteTimeUtc);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            File.SetCreationTimeUtc(destinationPath, source.CreationTimeUtc);
        }

        if (OperatingSystem.IsWindows())
        {
            var current = File.GetAttributes(destinationPath);
            var wanted = (current & ~CopiedAttributes) | (source.Attributes & CopiedAttributes);
            if (wanted != current)
            {
                File.SetAttributes(destinationPath, wanted);
            }
        }
    }

    public long? GetAvailableFreeSpace(string existingPath)
    {
        try
        {
            return FindDrive(existingPath)?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? GetVolumeFormat(string path)
    {
        try
        {
            return FindDrive(path)?.DriveFormat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The volume holding the path. On Windows the drive letter decides (UNC paths are unknown);
    /// elsewhere the mount point with the longest matching root. Only the chosen drive is probed.
    /// </summary>
    private static DriveInfo? FindDrive(string path)
    {
        var fullPath = Path.GetFullPath(path);
        DriveInfo? drive;

        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            drive = new DriveInfo(root);
        }
        else
        {
            var comparison = OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            drive = DriveInfo.GetDrives()
                .Where(d => fullPath.StartsWith(d.Name, comparison))
                .MaxBy(d => d.Name.Length);
        }

        try
        {
            return drive is { IsReady: true } ? drive : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileSystemEntry ToEntry(FileSystemInfo info)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        var isLink = (info.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(info);
        var length = !isDirectory && info is FileInfo file ? file.Length : 0;
        return new FileSystemEntry(
            info.Name,
            isDirectory ? EntryKind.Directory : EntryKind.File,
            length,
            info.LastWriteTimeUtc,
            isLink);
    }

    public Stream OpenRead(string path) => new FileStream(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.SequentialScan,
        BufferSize = 0,
    });

    // Only symbolic links and junctions have a link target. Other reparse points
    // (e.g. cloud placeholders, deduplicated files) are regular files for our purposes.
    private static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A reparse point we cannot inspect is safer treated as a link (not followed).
            return true;
        }
    }
}
