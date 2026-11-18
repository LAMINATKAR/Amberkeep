using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Scanning;

namespace Amberkeep.Core.Diff;

public enum DiffCategory
{
    /// <summary>Only on the source: copy to the target.</summary>
    New,

    /// <summary>Differs and the source is newer (or only the size differs): overwrite the target.</summary>
    Modified,

    /// <summary>Differs and the target is newer: overwrite the target. Not selected by default.</summary>
    TargetNewer,

    /// <summary>Content is equal, only the modification time differs (content compare mode): update the time on the target.</summary>
    TimestampOnly,

    /// <summary>Same content, name differs only in letter case: rename on the target.</summary>
    CaseRename,

    /// <summary>
    /// A file that is only on the source and a file that is only on the target are the same file (renamed folder,
    /// file moved to another folder, renamed file): move it on the target instead of copying it again.
    /// </summary>
    /// <remarks>Stored by name in history, so its position in this list does not matter for saved files.</remarks>
    Moved,

    /// <summary>Only on the target: remove (or move to the archive).</summary>
    Removed,

    /// <summary>The entry could not be compared. Nothing is done.</summary>
    Error,
}

public enum DiffSide
{
    Source,
    Target,
}

/// <summary>A problem with a path on one side that is not a change by itself (e.g. unreadable directory, link).</summary>
public sealed record DiffIssue(DiffSide Side, string RelativePath, string Message);

/// <summary>One proposed change.</summary>
public sealed record DiffItem
{
    /// <summary>Display path: the source path when the entry exists on the source, otherwise the target path.</summary>
    public required string RelativePath { get; init; }

    public required EntryKind Kind { get; init; }

    public required DiffCategory Category { get; init; }

    /// <summary>Entry on the source; null for <see cref="DiffCategory.Removed"/>.</summary>
    public ScannedEntry? Source { get; init; }

    /// <summary>Entry on the target; null for <see cref="DiffCategory.New"/>.</summary>
    /// <remarks>
    /// Its path can differ from <see cref="Source"/> in letter case; the target is then renamed to match. For
    /// <see cref="DiffCategory.Moved"/> it is the old place of the file on the target.
    /// </remarks>
    public ScannedEntry? Target { get; init; }

    /// <summary>Explanation for <see cref="DiffCategory.Error"/>.</summary>
    public string? Message { get; init; }

    public bool DefaultSelected => Category is not (DiffCategory.TargetNewer or DiffCategory.Error);
}
