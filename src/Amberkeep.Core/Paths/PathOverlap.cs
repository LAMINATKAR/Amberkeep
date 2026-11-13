namespace Amberkeep.Core.Paths;

public enum PathRelation
{
    Separate,
    Same,
    TargetInsideSource,
    SourceInsideTarget,
}

/// <summary>Detects source and target folders that are the same or nested in each other.</summary>
public static class PathOverlap
{
    public static PathRelation Check(string sourcePath, string targetPath, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var source = Prepare(sourcePath);
        var target = Prepare(targetPath);

        if (string.Equals(source, target, comparison))
        {
            return PathRelation.Same;
        }

        if (target.StartsWith(source, comparison))
        {
            return PathRelation.TargetInsideSource;
        }

        if (source.StartsWith(target, comparison))
        {
            return PathRelation.SourceInsideTarget;
        }

        return PathRelation.Separate;
    }

    // Full path with exactly one trailing separator, so "C:\data" is not treated as a prefix of "C:\data2".
    private static string Prepare(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        full = PathKey.Normalize(full);
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }
}
