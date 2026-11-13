using Amberkeep.Core.Paths;
using Amberkeep.Core.Safety;

namespace Amberkeep.Core.Diff;

public enum CompareMode
{
    /// <summary>Fast: files are equal when size matches and modification times differ by at most the tolerance.</summary>
    SizeAndTime,

    /// <summary>Exact: files of equal size are compared byte by byte.</summary>
    Content,
}

public sealed record DiffOptions
{
    public static DiffOptions Default { get; } = new();

    public CompareMode CompareMode { get; init; } = CompareMode.SizeAndTime;

    /// <summary>FAT and exFAT store modification times with 2 s precision.</summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// FAT stores local time, so after a daylight-saving change all times differ by exactly one hour.
    /// When on and the source or target is on a FAT volume, files of equal size whose times differ by
    /// one hour (± tolerance) count as unchanged. Other file systems store UTC and are not affected.
    /// </summary>
    public bool IgnoreOneHourShift { get; init; } = true;

    /// <summary>Exclusion patterns (see <see cref="Exclusions.ExclusionMatcher"/>). Apply to source and target alike.</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = [];

    public CaseSensitivity CaseSensitivity { get; init; } = CaseSensitivity.Auto;

    public MassRemovalGuard MassRemovalGuard { get; init; } = new();
}

public sealed record DiffRequest(string SourcePath, string TargetPath, DiffOptions Options);
