using System.Text;

namespace Amberkeep.Core.Paths;

/// <summary>
/// Helpers for relative paths. Relative paths always use '/' as separator, independent of the platform.
/// </summary>
public static class PathKey
{
    public const char Separator = '/';

    /// <summary>
    /// Normalizes a relative path for matching: Unicode form C, so that names stored decomposed
    /// (typical on macOS) match the same names stored composed (typical on Windows).
    /// </summary>
    public static string Normalize(string relativePath)
    {
        try
        {
            return relativePath.IsNormalized(NormalizationForm.FormC)
                ? relativePath
                : relativePath.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Names with invalid UTF-16 (possible on some file systems) cannot be normalized; use as is.
            return relativePath;
        }
    }

    /// <summary>
    /// True when Windows would not use <paramref name="name"/> inside <paramref name="folder"/> as written: it drops a
    /// trailing dot or space (<c>file.</c> means <c>file</c>), or it treats the name as a device (always <c>NUL</c>;
    /// on Windows 10 also e.g. <c>CON</c> or <c>aux.c</c>). Such names can exist on disks written by another system,
    /// and an operation on them would hit a different file. Asks Windows itself, so the rules of the running version
    /// apply. Always false on other systems.
    /// </summary>
    /// <param name="folder">Full path of the folder holding the entry.</param>
    /// <param name="name">Name of the entry.</param>
    public static bool IsChangedByWindows(string folder, string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (name.Length == 0 || name.EndsWith('.') || name.EndsWith(' '))
        {
            return true;
        }

        try
        {
            var expected = Path.Join(Path.GetFullPath(folder), name);
            return !string.Equals(Path.GetFullPath(expected), expected, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    public static string Combine(string parent, string name) =>
        parent.Length == 0 ? name : string.Concat(parent, "/", name);

    public static string GetName(string relativePath)
    {
        var index = relativePath.LastIndexOf(Separator);
        return index < 0 ? relativePath : relativePath[(index + 1)..];
    }

    /// <summary>Converts a relative path to a full platform path under <paramref name="root"/>.</summary>
    public static string ToSystemPath(string root, string relativePath) =>
        relativePath.Length == 0
            ? root
            : Path.Join(root, relativePath.Replace(Separator, Path.DirectorySeparatorChar));

    /// <summary>True when <paramref name="key"/> or any of its ancestors is in <paramref name="set"/>.</summary>
    public static bool IsSelfOrUnder(IReadOnlySet<string> set, string key)
    {
        if (set.Count == 0)
        {
            return false;
        }

        if (set.Contains(key))
        {
            return true;
        }

        var index = key.IndexOf(Separator);
        while (index >= 0)
        {
            if (set.Contains(key[..index]))
            {
                return true;
            }

            index = key.IndexOf(Separator, index + 1);
        }

        return false;
    }
}
