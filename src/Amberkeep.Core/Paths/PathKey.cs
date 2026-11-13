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
