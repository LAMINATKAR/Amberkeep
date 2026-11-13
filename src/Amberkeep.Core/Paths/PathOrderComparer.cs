namespace Amberkeep.Core.Paths;

/// <summary>
/// Orders relative paths segment by segment, so that every directory is immediately followed by its contents
/// ("a", "a/b", "a b" rather than "a", "a b", "a/b").
/// </summary>
public sealed class PathOrderComparer : IComparer<string>
{
    public static PathOrderComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var startX = 0;
        var startY = 0;
        while (true)
        {
            var endX = x.IndexOf(PathKey.Separator, startX);
            var endY = y.IndexOf(PathKey.Separator, startY);
            var segmentX = x.AsSpan(startX, (endX < 0 ? x.Length : endX) - startX);
            var segmentY = y.AsSpan(startY, (endY < 0 ? y.Length : endY) - startY);

            var result = segmentX.CompareTo(segmentY, StringComparison.OrdinalIgnoreCase);
            if (result == 0)
            {
                result = segmentX.CompareTo(segmentY, StringComparison.Ordinal);
            }

            if (result != 0)
            {
                return result;
            }

            if (endX < 0 || endY < 0)
            {
                return (endX < 0 ? 0 : 1) - (endY < 0 ? 0 : 1);
            }

            startX = endX + 1;
            startY = endY + 1;
        }
    }
}
