using Amberkeep.Core.FileSystem;

namespace Amberkeep.Core.Diff;

public sealed record CategoryTotals(int Files, int Directories, long Bytes);

/// <summary>Totals of a diff. Byte totals count only selected items, so the UI can recompute them.</summary>
public sealed class DiffSummary
{
    private DiffSummary(
        IReadOnlyDictionary<DiffCategory, CategoryTotals> byCategory,
        int unchangedFiles,
        long bytesToCopy,
        long bytesToReplace,
        long bytesToRemove,
        long bytesToMove)
    {
        ByCategory = byCategory;
        UnchangedFiles = unchangedFiles;
        BytesToCopy = bytesToCopy;
        BytesToReplace = bytesToReplace;
        BytesToRemove = bytesToRemove;
        BytesToMove = bytesToMove;
    }

    /// <summary>Counts of all items per category, selected or not.</summary>
    public IReadOnlyDictionary<DiffCategory, CategoryTotals> ByCategory { get; }

    public int UnchangedFiles { get; }

    /// <summary>Bytes written to the target (new and overwritten files).</summary>
    public long BytesToCopy { get; }

    /// <summary>Bytes of target files that get overwritten.</summary>
    public long BytesToReplace { get; }

    /// <summary>Bytes of target files that get removed.</summary>
    public long BytesToRemove { get; }

    /// <summary>Bytes of files moved on the target instead of copied again; they need no space and no copying.</summary>
    public long BytesToMove { get; }

    /// <summary>
    /// Free space needed on the target. With the archive on, nothing is freed (old versions are moved to the archive).
    /// Without it, removals free space; savings from overwritten files are ignored to stay on the safe side.
    /// </summary>
    public long RequiredFreeSpace(bool archiveEnabled) =>
        archiveEnabled ? BytesToCopy : Math.Max(0, BytesToCopy - BytesToRemove);

    public CategoryTotals Get(DiffCategory category) =>
        ByCategory.TryGetValue(category, out var totals) ? totals : new CategoryTotals(0, 0, 0);

    public static DiffSummary Compute(IEnumerable<DiffItem> items, Func<DiffItem, bool> isSelected, int unchangedFiles)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isSelected);

        var byCategory = new Dictionary<DiffCategory, CategoryTotals>();
        long copy = 0, replace = 0, remove = 0, move = 0;

        foreach (var item in items)
        {
            var isFile = item.Kind == EntryKind.File;
            var size = isFile ? (item.Source ?? item.Target)?.Length ?? 0 : 0;
            var current = byCategory.TryGetValue(item.Category, out var totals) ? totals : new CategoryTotals(0, 0, 0);
            byCategory[item.Category] = isFile
                ? current with { Files = current.Files + 1, Bytes = current.Bytes + size }
                : current with { Directories = current.Directories + 1 };

            if (!isFile || !isSelected(item))
            {
                continue;
            }

            switch (item.Category)
            {
                case DiffCategory.New:
                    copy += item.Source!.Length;
                    break;
                case DiffCategory.Modified or DiffCategory.TargetNewer:
                    copy += item.Source!.Length;
                    replace += item.Target!.Length;
                    break;
                case DiffCategory.Removed:
                    remove += item.Target!.Length;
                    break;
                case DiffCategory.Moved:
                    move += item.Source!.Length;
                    break;
            }
        }

        return new DiffSummary(byCategory, unchangedFiles, copy, replace, remove, move);
    }
}
