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
    private AppServices(string baseDirectory, IWritableFileSystem fileSystem, IVolumeProvider volumes, TimeProvider time)
    {
        FileSystem = fileSystem;
        Volumes = volumes;
        Time = time;
        Store = new AppStore(baseDirectory, fileSystem, time);
        History = new HistoryStore(Store, fileSystem);
        Registry = new TargetRegistry(fileSystem, volumes, time);
        Locator = new TargetLocator(fileSystem, volumes);
        Runner = new JobRunner(fileSystem, volumes, History, time);
        Cleanup = new CleanupService(Store, History, fileSystem, volumes, NoAutostart.Instance);
    }

    public IWritableFileSystem FileSystem { get; }

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

    public static AppServices Create(string baseDirectory) =>
        Create(baseDirectory, PhysicalFileSystem.Instance, PhysicalVolumeProvider.Instance, TimeProvider.System);

    public static AppServices Create(string baseDirectory, IWritableFileSystem fileSystem, IVolumeProvider volumes, TimeProvider time)
    {
        var services = new AppServices(baseDirectory, fileSystem, volumes, time);
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

        Store.Save(config);
        Accept(config);
    }

    /// <summary>Makes an already saved configuration current (e.g. returned by the clean-up service).</summary>
    public void Accept(AppConfig config)
    {
        Config = config;
        Runner.HistoryLimit = config.Settings.HistoryLimit;
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
        else if (result.Warning is not null)
        {
            StartupMessage = result.Warning;
        }
    }
}
