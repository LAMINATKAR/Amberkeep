using Amberkeep.Core.FileSystem;
using Amberkeep.Storage;
using Amberkeep.Storage.Cleanup;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.History;
using Amberkeep.Storage.Running;
using Amberkeep.Storage.Targets;

namespace Amberkeep.App.Services;

/// <summary>Everything the view models need, created once at start-up.</summary>
public sealed class AppServices
{
    private AppServices(string baseDirectory, IWritableFileSystem fileSystem, IVolumeProvider volumes, TimeProvider time, IAutostart autostart)
    {
        Autostart = autostart;
        FileSystem = fileSystem;
        Volumes = volumes;
        Time = time;
        Store = new AppStore(baseDirectory, fileSystem, time);
        History = new HistoryStore(Store, fileSystem);
        Registry = new TargetRegistry(fileSystem, volumes, time) { ProgramDirectory = Store.BaseDirectory };
        Locator = new TargetLocator(fileSystem, volumes);
        Runner = new JobRunner(fileSystem, volumes, History, time) { ProgramDirectory = Store.BaseDirectory, CurrentConfig = () => Config };
        Cleanup = new CleanupService(Store, History, fileSystem, volumes, autostart);
    }

    public IWritableFileSystem FileSystem { get; }

    public IAutostart Autostart { get; }

    public ActivityMonitor Activity { get; } = new();

    public RunGate Gate { get; } = new();

    public PendingReviews PendingReviews { get; } = new();

    public IVolumeProvider Volumes { get; }

    public TimeProvider Time { get; }

    public AppStore Store { get; }

    public HistoryStore History { get; }

    public TargetRegistry Registry { get; }

    public TargetLocator Locator { get; }

    public JobRunner Runner { get; }

    public CleanupService Cleanup { get; }

    public AppConfig Config { get; private set; } = new();

    /// <summary>Settings cannot be saved (program folder not writable, or settings from a newer version).</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>Message shown once on the dashboard after start-up.</summary>
    public string? StartupMessage { get; private set; }

    public bool StartupMessageIsError { get; private set; }

    public static AppServices Create(string baseDirectory, IAutostart autostart) =>
        Create(baseDirectory, PhysicalFileSystem.Instance, PhysicalVolumeProvider.Instance, TimeProvider.System, autostart);

    public static AppServices Create(
        string baseDirectory, IWritableFileSystem fileSystem, IVolumeProvider volumes, TimeProvider time, IAutostart? autostart = null)
    {
        var services = new AppServices(baseDirectory, fileSystem, volumes, time, autostart ?? NoAutostart.Instance);
        services.Load();
        return services;
    }

    /// <summary>Saves the configuration and makes it current.</summary>
    /// <exception cref="InvalidOperationException">The settings are read-only.</exception>
    public void Save(AppConfig config)
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException(StartupMessage ?? "Settings are read-only.");
        }

        lock (_saveLock)
        {
            Store.Save(config);
            Accept(config);
        }
    }

    // Saves come from the UI and from background runs; one at a time (the lock is re-entrant for Update).
    private readonly Lock _saveLock = new();

    /// <summary>Makes an already saved configuration current (e.g. returned by the clean-up service).</summary>
    /// <summary>
    /// Changes the current configuration and saves it, serialized with other updates, so that a background
    /// change (e.g. a target found under a new drive letter) and a change made in the UI do not overwrite each other.
    /// </summary>
    public void Update(Func<AppConfig, AppConfig> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_saveLock)
        {
            Save(change(Config));
        }
    }

    public void Accept(AppConfig config)
    {
        Config = config;
        Runner.HistoryLimit = config.Settings.HistoryLimit;
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after the configuration changed (possibly on a background thread).</summary>
    public event EventHandler? ConfigChanged;

    /// <summary>
    /// Makes the system autostart entry match the settings: creates or refreshes it (the program may have moved)
    /// when "start with the system" is on, removes it otherwise. Returns a problem description, or null.
    /// </summary>
    public string? SyncAutostart()
    {
        if (IsReadOnly)
        {
            // The settings are not ours (newer version or not writable); leave the entry as it is.
            return null;
        }

        try
        {
            if (Config.Settings.StartWithSystem)
            {
                Autostart.Enable();
            }
            else if (Autostart.IsEnabled)
            {
                Autostart.Remove();
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return $"The autostart entry could not be updated: {ex.Message}";
        }
    }

    private void Load()
    {
        var notWritable = Store.CheckWritable();
        var result = Store.Load();

        if (result.Error is not null)
        {
            IsReadOnly = true;
            StartupMessage = result.Error;
            StartupMessageIsError = true;
            return;
        }

        Accept(result.Config!);
        if (notWritable is not null)
        {
            IsReadOnly = true;
            StartupMessage = notWritable;
            StartupMessageIsError = true;
        }
        else
        {
            if (result.Warning is not null)
            {
                StartupMessage = result.Warning;
            }

            // Corrected values (e.g. new ids for duplicates) are saved at once, so they stay the same next time.
            if (result.Corrected)
            {
                try
                {
                    Store.Save(result.Config!);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // Not saved: corrected again on the next start.
                }
            }
        }
    }
}
