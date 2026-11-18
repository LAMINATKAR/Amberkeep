namespace Amberkeep.App.Services;

/// <summary>Recognizes files that run code when they are opened, so opening one is confirmed first.</summary>
public static class FileTypes
{
    /// <summary>Programs, scripts, installers and shortcuts that Windows runs or follows instead of showing.</summary>
    private static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".psc1", ".msi", ".msp", ".mst", ".scr", ".lnk", ".url",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".cpl", ".jar", ".reg", ".msc", ".pif", ".application",
        ".appref-ms", ".gadget", ".inf", ".settingcontent-ms", ".library-ms", ".scf", ".sh", ".command", ".app",
        ".desktop", ".appimage", ".run", ".py", ".pyw",
    };

    /// <summary>True when opening <paramref name="path"/> would run a program or script rather than show a document.</summary>
    /// <param name="pathExt">The <c>PATHEXT</c> variable (e.g. <c>.COM;.EXE;.BAT</c>); read from the environment when null.</param>
    public static bool IsProgram(string path, string? pathExt = null)
    {
        var extension = Path.GetExtension(path.TrimEnd('.', ' '));
        if (extension.Length == 0)
        {
            return false;
        }

        if (Programs.Contains(extension))
        {
            return true;
        }

        pathExt ??= Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty;
        return pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
