using System.Globalization;
using System.Text.Json;
using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;

namespace Amberkeep.Storage;

/// <param name="Config">Loaded configuration; null when it must not be used (see <paramref name="Error"/>).</param>
/// <param name="Warning">Something the user should know, e.g. a damaged file was replaced by defaults.</param>
/// <param name="Error">The configuration cannot be used and must not be overwritten.</param>
public sealed record LoadResult(AppConfig? Config, string? Warning, string? Error);

/// <summary>
/// Files Amberkeep keeps next to its executable (SPEC 5): <c>amberkeep.json</c>, <c>history/</c>, <c>state/</c>.
/// </summary>
public sealed class AppStore(string baseDirectory, IWritableFileSystem fileSystem, TimeProvider timeProvider)
{
    public const string ConfigFileName = "amberkeep.json";
    public const string HistoryFolderName = "history";
    public const string StateFolderName = "state";
    internal const string WriteProbePrefix = ".amberkeep-write-test-";

    private bool _saveBlocked;

    public AppStore(string baseDirectory)
        : this(baseDirectory, PhysicalFileSystem.Instance, TimeProvider.System)
    {
    }

    public string BaseDirectory { get; } = Path.GetFullPath(baseDirectory);

    public string ConfigPath => Path.Join(BaseDirectory, ConfigFileName);

    public string HistoryDirectory => Path.Join(BaseDirectory, HistoryFolderName);

    public string StateDirectory => Path.Join(BaseDirectory, StateFolderName);

    /// <summary>Folder of the running executable – where the app keeps its data.</summary>
    public static string ExecutableDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>Checks that files can be written next to the program; returns the problem, or null.</summary>
    public string? CheckWritable()
    {
        var probe = Path.Join(BaseDirectory, $"{WriteProbePrefix}{Guid.NewGuid():N}");
        try
        {
            using (fileSystem.CreateFile(probe))
            {
            }

            fileSystem.DeleteFile(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Amberkeep cannot write next to the program ({BaseDirectory}): {ex.Message} " +
                   "Move Amberkeep to a folder you can write to.";
        }
    }

    public LoadResult Load()
    {
        if (fileSystem.GetEntry(ConfigPath) is null)
        {
            return new LoadResult(new AppConfig(), null, null);
        }

        AppConfig? config;
        try
        {
            config = StorageFiles.Read(fileSystem, ConfigPath, StorageJsonContext.Default.AppConfig);
        }
        catch (JsonException ex)
        {
            return SetAsideDamaged($"the file is not valid ({ex.Message})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LoadResult(null, null, $"Settings cannot be read: {ex.Message}");
        }

        if (config is null)
        {
            return SetAsideDamaged("the file is empty");
        }

        if (config.Version > AppConfig.CurrentVersion)
        {
            _saveBlocked = true;
            return new LoadResult(
                null,
                null,
                $"Settings were saved by a newer version of Amberkeep (format {config.Version}). Update Amberkeep; the file was left untouched.");
        }

        return new LoadResult(config.Normalized(), null, null);
    }

    /// <exception cref="InvalidOperationException">The file on disk was written by a newer version and must not be overwritten.</exception>
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_saveBlocked)
        {
            throw new InvalidOperationException("Settings were saved by a newer version of Amberkeep and are not overwritten.");
        }

        StorageFiles.Write(fileSystem, ConfigPath, config with { Version = AppConfig.CurrentVersion }, StorageJsonContext.Default.AppConfig);
    }

    private LoadResult SetAsideDamaged(string reason)
    {
        var stamp = timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var damaged = $"{ConfigPath}.bad-{stamp}";
        try
        {
            fileSystem.MoveFile(ConfigPath, damaged, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LoadResult(null, null, $"Settings are damaged ({reason}) and cannot be set aside: {ex.Message}");
        }

        return new LoadResult(
            new AppConfig(),
            $"Settings were damaged ({reason}). They were saved as {Path.GetFileName(damaged)} and defaults are used.",
            null);
    }
}
