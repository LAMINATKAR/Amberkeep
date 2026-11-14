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
    /// <summary>
    /// Compares the paths as written and, when <paramref name="onDisk"/> is set, also where they really are on disk
    /// (<see cref="PathIdentity"/>), so a junction, link, <c>subst</c> drive, short name or share of the local disk
    /// cannot hide an overlap.
    /// </summary>
    public static PathRelation Check(string sourcePath, string targetPath, bool caseSensitive, bool onDisk = false)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var relation = Compare(sourcePath, targetPath, comparison);
        if (relation != PathRelation.Separate || !onDisk)
        {
            return relation;
        }

        var realSource = PathIdentity.TryGetRealPath(sourcePath);
        var realTarget = PathIdentity.TryGetRealPath(targetPath);
        if (realSource is not null && realTarget is not null)
        {
            relation = Compare(realSource, realTarget, comparison);
            if (relation != PathRelation.Separate)
            {
                return relation;
            }
        }

        // The same folder reached through a share has a different real path, but the same identity.
        var sourceId = PathIdentity.TryGetDirectoryId(sourcePath);
        if (sourceId is not null && AnySelfOrParentHasId(realTarget ?? targetPath, sourceId))
        {
            return PathIdentity.TryGetDirectoryId(targetPath) == sourceId ? PathRelation.Same : PathRelation.TargetInsideSource;
        }

        var targetId = PathIdentity.TryGetDirectoryId(targetPath);
        if (targetId is not null && AnySelfOrParentHasId(realSource ?? sourcePath, targetId))
        {
            return PathRelation.SourceInsideTarget;
        }

        return PathRelation.Separate;
    }

    private static bool AnySelfOrParentHasId(string path, string id)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (PathIdentity.TryGetDirectoryId(current) == id)
            {
                return true;
            }
        }

        return false;
    }

    private static PathRelation Compare(string sourcePath, string targetPath, StringComparison comparison)
    {
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
