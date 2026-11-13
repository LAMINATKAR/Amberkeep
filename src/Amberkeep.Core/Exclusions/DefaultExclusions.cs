namespace Amberkeep.Core.Exclusions;

/// <summary>System files and folders that should never be backed up. Offered as defaults for a new job.</summary>
public static class DefaultExclusions
{
    public static IReadOnlyList<string> Patterns { get; } =
    [
        // Windows
        "$RECYCLE.BIN/",
        "System Volume Information/",
        "pagefile.sys",
        "hiberfil.sys",
        "swapfile.sys",
        "Thumbs.db",

        // macOS
        ".DS_Store",
        ".Trashes/",
        ".Spotlight-V100/",
        ".fseventsd/",
    ];
}
