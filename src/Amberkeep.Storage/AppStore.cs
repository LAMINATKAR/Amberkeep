using System.Globalization;
using System.Text.Json;
using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;

namespace Amberkeep.Storage;

/// <param name="Config">Loaded configuration; null when it must not be used (see <paramref name="Error"/>).</param>
/// <param name="Warning">Something the user should know, e.g. a damaged file was replaced by defaults.</param>
/// <param name="Error">The configuration cannot be used and must not be overwritten.</param>
public sealed record LoadResult(AppConfig? Config, string? Warning, string? Error)
{
    /// <summary>Invalid values were corrected while loading; the caller should save the corrected settings.</summary>
    public bool Corrected { get; init; }
}

/// <summary>
/// Files Amberkeep keeps next to its executable (SPEC 5): <c>amberkeep.json</c>, <c>history/</c>, <c>state/</c>.
/// </summary>
public sealed partial class AppStore(string baseDirectory, IWritableFileSystem fileSystem, TimeProvider timeProvider)
{
    public const string ConfigFileName = "amberkeep.json";
    public const string HistoryFolderName = "history";
    public const string StateFolderName = "state";
    internal const string WriteProbePrefix = ".amberkeep-write-test-";

    /// <summary>Log of unexpected errors, written by the app next to the executable.</summary>
    public const string ErrorLogFileName = "amberkeep-error.log";

    /// <summary>
    /// A file next to the executable that Amberkeep wrote: <c>amberkeep.json</c>, its set-aside damaged copies
    /// (<c>.bad-&lt;time&gt;</c>), interrupted saves (<c>.&lt;id&gt;.tmp</c>), write tests and the error log.
    /// A user's own copy such as <c>amberkeep.json.mine</c> is not.
    /// </summary>
    public static bool IsOwnFile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Equals(ConfigFileName, StringComparison.OrdinalIgnoreCase)
               || name.Equals(ErrorLogFileName, StringComparison.OrdinalIgnoreCase)
               || name.StartsWith(WriteProbePrefix, StringComparison.OrdinalIgnoreCase)
               || OwnConfigVariant().IsMatch(name);
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^amberkeep\.json\.(bad-\d{8}-\d{6}(-\d+)?|[0-9a-f]{32}\.tmp)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex OwnConfigVariant();

    private bool _saveBlocked;

    public AppStore(string baseDirectory)
        : this(baseDirectory, PhysicalFileSystem.Instance, TimeProvider.System)
    {
    }

    public string BaseDirectory { get; } = Path.GetFullPath(baseDirectory);

    public string ConfigPath => Path.Join(BaseDirectory, ConfigFileName);

    public string HistoryDirectory => Path.Join(BaseDirectory, HistoryFolderName);

    public string StateDirectory => Path.Join(BaseDirectory, StateFolderName);

    /// <summary>Folder of the running executable – where the app keeps its data (also for a single-file build).</summary>
    public static string ExecutableDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

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

    private static string NewerVersionMessage(int version) =>
        $"Settings were saved by a newer version of Amberkeep (format {version}). Update Amberkeep; the file was left untouched.";

    /// <summary>Only the format version of the file, read without the rest; null when it cannot be read.</summary>
    private int? ReadVersion()
    {
        try
        {
            using var stream = fileSystem.OpenRead(ConfigPath);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("version", out var version)
                   && version.TryGetInt32(out var number)
                ? number
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
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
            // A newer version may have added values this version cannot read: that is not damage.
            if (ReadVersion() > AppConfig.CurrentVersion)
            {
                _saveBlocked = true;
                return new LoadResult(null, null, NewerVersionMessage(ReadVersion()!.Value));
            }

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
            return new LoadResult(null, null, NewerVersionMessage(config.Version));
        }

        var fixes = new List<string>();
        var normalized = config.Normalized(fixes);
        return new LoadResult(
            normalized,
            fixes.Count == 0 ? null : "Some settings in amberkeep.json were invalid and were corrected: " + string.Join(" ", fixes),
            null) { Corrected = fixes.Count > 0 };
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
