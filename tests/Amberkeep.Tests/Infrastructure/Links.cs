namespace Amberkeep.Tests.Infrastructure;

internal static class Links
{
    /// <summary>Creates a directory symlink or skips the test when the OS does not allow it (Windows without developer mode).</summary>
    public static void CreateDirectoryLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Cannot create symbolic links here: {ex.Message}");
        }
    }
}
