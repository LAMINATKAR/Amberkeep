using System.Diagnostics;

namespace Amberkeep.Tests.Infrastructure;

internal static class Links
{
    /// <summary>
    /// Creates a directory link: a junction on Windows (needs no privilege, unlike a symbolic link) and a symbolic
    /// link elsewhere. Skips the test only when the system refuses both.
    /// </summary>
    public static void CreateDirectoryLinkOrSkip(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            mklink.WaitForExit();
            if (mklink.ExitCode == 0 && Directory.Exists(linkPath))
            {
                return;
            }
        }

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Cannot create directory links here: {ex.Message}");
        }
    }

    /// <summary>Removes a directory link without touching what it points to.</summary>
    public static void RemoveDirectoryLink(string linkPath)
    {
        if (Directory.Exists(linkPath))
        {
            Directory.Delete(linkPath, recursive: false);
        }
    }
}
