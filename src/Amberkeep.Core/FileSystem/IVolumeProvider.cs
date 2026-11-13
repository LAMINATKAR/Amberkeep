namespace Amberkeep.Core.FileSystem;

/// <summary>Lists the roots of mounted volumes (drive letters on Windows, mount points elsewhere).</summary>
public interface IVolumeProvider
{
    IReadOnlyList<string> GetVolumeRoots();
}

/// <summary>
/// Local volumes only: network and optical drives are skipped, because probing them can block for seconds.
/// A target on a network share is still found at its last known path.
/// </summary>
public sealed class PhysicalVolumeProvider : IVolumeProvider
{
    public static PhysicalVolumeProvider Instance { get; } = new();

    public IReadOnlyList<string> GetVolumeRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory) && drive.IsReady)
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not accessible right now; treated as not mounted.
            }
        }

        return roots;
    }
}
