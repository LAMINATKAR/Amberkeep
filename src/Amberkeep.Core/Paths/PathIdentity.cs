using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Amberkeep.Core.Paths;

/// <summary>
/// Where a folder really is on disk. Two different path strings can name the same folder: a junction or symbolic
/// link, a <c>subst</c> drive, an 8.3 short name, a <c>\\?\</c> prefix or a share of this computer's own disk.
/// Overlap checks compare these real locations as well, so such an alias can never make a target contain its own
/// source. Read-only: nothing is created or changed.
/// </summary>
public static class PathIdentity
{
    /// <summary>
    /// The real full path of <paramref name="path"/> with every link, junction and drive alias resolved; a share of
    /// this computer is mapped back to its local folder. When the path does not exist (yet), the nearest existing
    /// parent is resolved and the rest is appended. Null when nothing along the path exists or it cannot be resolved.
    /// </summary>
    public static string? TryGetRealPath(string path)
    {
        if (Split(path) is not { } split)
        {
            return null;
        }

        var (existing, missing) = split;

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
    /// True for a path that leads through a network share to a disk of this computer, but whose local folder could
    /// not be determined. Overlap checks then cannot be sure and treat it as overlapping.
    /// </summary>
    public static bool IsUnresolvedShareOfThisComputer(string path)
    {
        if (!OperatingSystem.IsWindows() || Split(path) is not { } split)
        {
            return false;
        }

        using var handle = OpenDirectory(split.Existing);
        if (handle.IsInvalid || FinalPath(handle) is not { } final || !final.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        // A share whose volume is one of ours, but which could not be mapped to a local folder.
        return GetFileId(handle) is { } id
               && LocalVolumes().Any(v => v.Volume == id.Volume)
               && MapShareToLocal(handle) is null;
    }

    /// <summary>
    /// An identifier of an existing folder (volume serial number and file id), equal for every path that leads
    /// to the same folder, including a share of this computer. Null when it cannot be determined.
    /// </summary>
    public static string? TryGetDirectoryId(string path) =>
        OperatingSystem.IsWindows() && Directory.Exists(path) && GetFileId(path) is { } id
            ? $"{id.Volume:X16}-{id.File}"
            : null;

    // The nearest existing folder of a path and the names below it that do not exist yet.
    private static (string Existing, Stack<string> Missing)? Split(string path)
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

        return (existing, missing);
    }

    [SupportedOSPlatform("windows")]
    private static string? FinalPathWindows(string path)
    {
        using var handle = OpenDirectory(path);
        if (handle.IsInvalid)
        {
            return null;
        }

        var final = FinalPath(handle);
        if (final is null || !final.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return final;
        }

        // A share: if it is a disk of this computer, find the same folder through the local disk.
        return MapShareToLocal(handle) ?? final;
    }

    [SupportedOSPlatform("windows")]
    private static string? FinalPath(SafeFileHandle handle)
    {
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

    // Same volume serial number on a local disk: open the folder there by its file id.
    [SupportedOSPlatform("windows")]
    private static string? MapShareToLocal(SafeFileHandle shared)
    {
        if (GetFileId(shared) is not { } id)
        {
            return null;
        }

        foreach (var (root, _) in LocalVolumes().Where(v => v.Volume == id.Volume))
        {
            using var rootHandle = OpenDirectory(root);
            if (rootHandle.IsInvalid)
            {
                continue;
            }

            var descriptor = new FileIdDescriptor { Size = (uint)Marshal.SizeOf<FileIdDescriptor>(), Type = ExtendedFileIdType, FileId = id.Raw };
            using var local = OpenFileById(rootHandle, ref descriptor, FileReadAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileFlagBackupSemantics);
            if (!local.IsInvalid && FinalPath(local) is { } path && !path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return path;
            }
        }

        return null;
    }

    private static readonly Lock VolumesLock = new();
    private static (DateTime Read, List<(string Root, ulong Volume)> Volumes)? _volumes;

    /// <summary>
    /// Local disks (fixed and removable) with their volume serial numbers, re-read at most every 10 seconds.
    /// Only those drive types are probed: asking a disconnected network drive or an empty optical drive whether
    /// it is ready can take seconds.
    /// </summary>
    private static List<(string Root, ulong Volume)> LocalVolumes()
    {
        lock (VolumesLock)
        {
            if (_volumes is { } cached && DateTime.UtcNow - cached.Read < TimeSpan.FromSeconds(10))
            {
                return cached.Volumes;
            }
        }

        var volumes = new List<(string Root, ulong Volume)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady
                    && GetFileId(drive.RootDirectory.FullName) is { } id)
                {
                    volumes.Add((drive.RootDirectory.FullName, id.Volume));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not a usable local disk.
            }
        }

        lock (VolumesLock)
        {
            _volumes = (DateTime.UtcNow, volumes);
        }

        return volumes;
    }

    private static FileId? GetFileId(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var handle = OpenDirectory(path);
        return handle.IsInvalid ? null : GetFileId(handle);
    }

    // 64-bit volume serial number and 128-bit file id (unique also on ReFS); an all-zero id means "unknown".
    [SupportedOSPlatform("windows")]
    private static FileId? GetFileId(SafeFileHandle handle)
    {
        if (GetFileInformationByHandleEx(handle, FileIdInfoClass, out var info, (uint)Marshal.SizeOf<FileIdInfo>())
            && info.FileId is { Length: 16 } raw && raw.Any(b => b != 0))
        {
            return new FileId(info.VolumeSerialNumber, Convert.ToHexString(raw), raw);
        }

        if (GetFileInformationByHandle(handle, out var basic) && (basic.FileIndexHigh | basic.FileIndexLow) != 0)
        {
            var raw16 = new byte[16];
            BitConverter.TryWriteBytes(raw16.AsSpan(0, 4), basic.FileIndexLow);
            BitConverter.TryWriteBytes(raw16.AsSpan(4, 4), basic.FileIndexHigh);
            return new FileId(basic.VolumeSerialNumber, $"{basic.FileIndexHigh:X8}{basic.FileIndexLow:X8}", raw16);
        }

        return null;
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

    private sealed record FileId(ulong Volume, string File, byte[] Raw);

    private const uint FileReadAttributes = 0x80;

    // Needed to open a directory; reparse points (junctions, links) are followed to their target.
    private const uint FileFlagBackupSemantics = 0x02000000;

    private const int FileIdInfoClass = 18;

    private const int ExtendedFileIdType = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor
    {
        public uint Size;
        public int Type;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint,
        ref FileIdDescriptor fileId,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint flagsAndAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder filePath, int filePathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdInfo information, uint bufferSize);
}
