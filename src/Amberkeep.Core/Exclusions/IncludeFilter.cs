using Amberkeep.Core.Paths;

namespace Amberkeep.Core.Exclusions;

/// <summary>Where a path stands with respect to the folders and files selected for a backup.</summary>
public enum IncludeScope
{
    /// <summary>Not selected: like an excluded item, neither copied nor removed on the target.</summary>
    Outside,

    /// <summary>A folder above a selected item: the folder itself is mirrored, its other contents are not.</summary>
    Container,

    /// <summary>Selected, or inside a selected folder: mirrored with everything in it.</summary>
    Included,
}

/// <summary>
/// "Back up only these folders and files" of a source (relative paths). Applied to the source and the target alike,
/// as exclusions are, so nothing outside the selection is ever copied, changed or removed on the target. An empty
/// selection means the whole folder.
/// </summary>
public sealed class IncludeFilter
{
    private readonly HashSet<string> _included;
    private readonly HashSet<string> _containers;

    /// <param name="paths">Selected paths, '/'- or '\'-separated, relative to the source. Must be <see cref="IsValidPath"/>.</param>
    /// <param name="caseSensitive">Names are compared by the backup's letter-case rule (and always in Unicode form C).</param>
    public IncludeFilter(IEnumerable<string> paths, bool caseSensitive)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _included = new HashSet<string>(comparer);
        _containers = new HashSet<string>(comparer);
        foreach (var path in paths)
        {
            if (!IsValidPath(path))
            {
                throw new ArgumentException($"Not a valid relative path: {path}", nameof(paths));
            }

            var key = Normalize(path);
            _included.Add(key);
            for (var slash = key.IndexOf(PathKey.Separator); slash >= 0; slash = key.IndexOf(PathKey.Separator, slash + 1))
            {
                _containers.Add(key[..slash]);
            }
        }

        Paths = _included.Order(StringComparer.Ordinal).ToList();
    }

    public static IncludeFilter All { get; } = new([], caseSensitive: false);

    /// <summary>Nothing is selected: the whole folder is backed up.</summary>
    public bool IsEmpty => _included.Count == 0;

    /// <summary>The selected paths, normalized ('/'-separated, Unicode form C).</summary>
    public IReadOnlyList<string> Paths { get; }

    public IncludeScope Classify(string relativePath, bool isDirectory)
    {
        if (IsEmpty)
        {
            return IncludeScope.Included;
        }

        var key = PathKey.Normalize(relativePath);
        if (PathKey.IsSelfOrUnder(_included, key))
        {
            return IncludeScope.Included;
        }

        return isDirectory && _containers.Contains(key) ? IncludeScope.Container : IncludeScope.Outside;
    }

    /// <summary>
    /// A path relative to the source that names something inside it: not empty, not rooted, no empty, <c>.</c> or
    /// <c>..</c> segments, and no characters a file name cannot contain.
    /// </summary>
    public static bool IsValidPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', PathKey.Separator).Trim(PathKey.Separator);
        if (normalized.Length == 0 || Path.IsPathRooted(path) || normalized.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var invalid = Path.GetInvalidFileNameChars();
        return normalized.Split(PathKey.Separator).All(segment =>
            segment.Length > 0 && segment != "." && segment != ".." && segment.IndexOfAny(invalid) < 0);
    }

    /// <summary>'/'-separated, without leading or trailing separators, in Unicode form C.</summary>
    public static string Normalize(string path) =>
        PathKey.Normalize(path.Replace('\\', PathKey.Separator).Trim(PathKey.Separator));

    /// <summary>
    /// The shortest equivalent selection: valid paths only, normalized, without duplicates and without paths inside
    /// another selected folder. Sorted.
    /// </summary>
    public static IReadOnlyList<string> Compact(IEnumerable<string> paths, bool caseSensitive)
    {
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var kept = new HashSet<string>(comparer);
        foreach (var path in paths.Where(IsValidPath).Select(Normalize).OrderBy(p => p.Length))
        {
            if (!PathKey.IsSelfOrUnder(kept, path))
            {
                kept.Add(path);
            }
        }

        return kept.Order(StringComparer.Ordinal).ToList();
    }
}
