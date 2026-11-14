using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Amberkeep.Core.Paths;

/// <summary>
/// Where a folder really is on disk. Two different path strings can name the same folder: a junction or symbolic
/// link, a <c>subst</c> drive, an 8.3 short name, a <c>\\?\</c> prefix or a share of the local disk. Overlap checks
/// compare these real locations as well, so such an alias can never make a target contain its own source.
/// Read-only: nothing is created or changed.
/// </summary>
public static class PathIdentity
{
    /// <summary>
    /// The real full path of <paramref name="path"/> with every link, junction and drive alias resolved.
    /// When the path does not exist (yet), the nearest existing parent is resolved and the rest is appended.
    /// Null when nothing along the path exists or it cannot be resolved.
    /// </summary>
    public static string? TryGetRealPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var missing = new Stack<string>();
        var existing = full;
        while (!Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return null;
            }

            missing.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = OperatingSystem.IsWindows() ? FinalPathWindows(existing) : FinalPathUnix(existing);
        if (resolved is null)
        {
            return null;
        }

        while (missing.Count > 0)
        {
            resolved = Path.Join(resolved, missing.Pop());
        }

        return resolved;
    }

    /// <summary>
    /// An identifier of an existing folder (volume serial number and file index), equal for every path that leads
    /// to the same folder, including a share of the local disk. Null when it cannot be determined.
    /// </summary>
    public static string? TryGetDirectoryId(string path)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(path))
        {
            return null;
        }

        using var handle = OpenDirectory(path);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        return $"{info.VolumeSerialNumber:X8}-{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    [SupportedOSPlatform("windows")]
    private static string? FinalPathWindows(string path)
    {
        using var handle = OpenDirectory(path);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new StringBuilder(512);
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Capacity)
            {
                break;
            }

            buffer.Capacity = (int)length + 1;
        }

        var result = buffer.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + result[8..];
        }

        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }

    // Resolves every link component from the root down.
    private static string? FinalPathUnix(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path) ?? string.Empty;
            var current = root;
            foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Join(current, part);
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = target.FullName;
                }
            }

            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenDirectory(string path) =>
        CreateFile(
            path,
            FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);

    private const uint FileReadAttributes = 0x80;

    // Needed to open a directory; reparse points (junctions, links) are followed to their target.
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder filePath, int filePathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
