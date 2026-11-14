using System.Text.Json;
using System.Text.Json.Serialization;
using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.History;
using Amberkeep.Storage.Targets;

namespace Amberkeep.Storage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ExportFile))]
[JsonSerializable(typeof(TargetMarker))]
[JsonSerializable(typeof(RunRecord))]
[JsonSerializable(typeof(List<RecordedChange>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;

/// <summary>Reads and writes our own JSON files; writes are atomic (temporary file + rename).</summary>
internal static class StorageFiles
{
    public static T? Read<T>(IFileSystem fileSystem, string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        using var stream = fileSystem.OpenRead(path);
        return JsonSerializer.Deserialize(stream, type);
    }

    public static void Write<T>(
        IWritableFileSystem fileSystem,
        string path,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        var folder = Path.GetDirectoryName(path)!;
        fileSystem.CreateDirectory(folder);
        var temp = Path.Join(folder, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = fileSystem.CreateFile(temp))
            {
                JsonSerializer.Serialize(stream, value, type);
                if (stream is FileStream file)
                {
                    file.Flush(flushToDisk: true);
                }
            }

            fileSystem.MoveFile(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (fileSystem.GetEntry(temp) is not null)
                {
                    fileSystem.DeleteFile(temp);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave the temporary file; the original stays intact.
            }

            throw;
        }
    }
}
