using Amberkeep.Core.FileSystem;

namespace Amberkeep.Core.Scanning;

/// <summary>A file or directory found by a scan.</summary>
/// <param name="RelativePath">Path relative to the scanned root, '/' separated, with the original name casing.</param>
public sealed record ScannedEntry(string RelativePath, EntryKind Kind, long Length, DateTime LastWriteTimeUtc);

/// <summary>A path that could not be scanned or was deliberately not followed.</summary>
public sealed record ScanIssue(string RelativePath, string Message);

public sealed record ScanProgress(int EntriesFound, string CurrentDirectory);

public sealed class ScanResult
{
    public required string RootPath { get; init; }

    public bool RootExists { get; init; }

    /// <summary>Set when the root exists but cannot be listed.</summary>
    public string? RootError { get; init; }

    public IReadOnlyList<ScannedEntry> Entries { get; init; } = [];

    /// <summary>Directories that could not be read. Their content is unknown.</summary>
    public IReadOnlyList<ScanIssue> Errors { get; init; } = [];

    /// <summary>Symbolic links and junctions, which are never followed. Their content is unknown.</summary>
    public IReadOnlyList<ScanIssue> Skipped { get; init; } = [];

    public int FileCount { get; init; }
}
