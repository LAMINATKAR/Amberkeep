using System.Text.Json;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Scanning;

namespace Amberkeep.Storage.Targets;

/// <summary>Content of <c>&lt;target&gt;/.amberkeep/target.json</c> – identifies a backup target (SPEC 4.9).</summary>
public sealed record TargetMarker
{
    public const int CurrentFormat = 1;
    public const string AppName = "Amberkeep";

    public int Format { get; init; } = CurrentFormat;

    public string App { get; init; } = AppName;

    public Guid TargetId { get; init; }

    public Guid JobId { get; init; }

    public string JobName { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; }
}

public enum MarkerState
{
    Missing,
    Unreadable,
    Present,
}

internal static class MarkerFile
{
    public const string FileName = "target.json";

    public static string ProgramFolder(string targetFolder) => Path.Join(targetFolder, Scanner.ProgramFolderName);

    public static string PathOf(string targetFolder) => Path.Join(ProgramFolder(targetFolder), FileName);

    public static (MarkerState State, TargetMarker? Marker) Read(IFileSystem fileSystem, string targetFolder)
    {
        var path = PathOf(targetFolder);
        if (!fileSystem.FileExists(path))
        {
            return (MarkerState.Missing, null);
        }

        try
        {
            var marker = StorageFiles.Read(fileSystem, path, StorageJsonContext.Default.TargetMarker);
            return marker is null ? (MarkerState.Unreadable, null) : (MarkerState.Present, marker);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (MarkerState.Unreadable, null);
        }
    }

    public static bool BelongsTo(IFileSystem fileSystem, string targetFolder, Guid targetId) =>
        Read(fileSystem, targetFolder) is (MarkerState.Present, { } marker) && marker.TargetId == targetId;

    public static void Write(IWritableFileSystem fileSystem, string targetFolder, TargetMarker marker) =>
        StorageFiles.Write(fileSystem, PathOf(targetFolder), marker, StorageJsonContext.Default.TargetMarker);
}
