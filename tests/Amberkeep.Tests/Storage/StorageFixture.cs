using Amberkeep.Core.FileSystem;
using Amberkeep.Storage;
using Amberkeep.Storage.Cleanup;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.History;
using Amberkeep.Storage.Running;
using Amberkeep.Storage.Targets;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.Storage;

internal sealed class FakeVolumes(params string[] roots) : IVolumeProvider
{
    public List<string> Roots { get; } = [.. roots];

    public IReadOnlyList<string> GetVolumeRoots() => Roots;
}

/// <summary>An "executable folder", two fake volumes and a source folder in one temp directory.</summary>
internal sealed class StorageFixture : IDisposable
{
    private readonly TempDirectory _temp = new();

    public StorageFixture()
    {
        AppDirectory = Create("app");
        VolumeA = Create("volA");
        VolumeB = Create("volB");
        Source = new TreeBuilder(Path.Combine(_temp.Path, "source"));
        Volumes = new FakeVolumes(VolumeA, VolumeB);
        Store = new AppStore(AppDirectory, FileSystem, Time);
        History = new HistoryStore(Store, FileSystem);
        Registry = new TargetRegistry(FileSystem, Volumes, Time);
        Locator = new TargetLocator(FileSystem, Volumes);
        Runner = new JobRunner(FileSystem, Volumes, History, Time);
        Cleanup = new CleanupService(Store, History, FileSystem, Volumes, NoAutostart.Instance);
    }

    public PhysicalFileSystem FileSystem { get; } = PhysicalFileSystem.Instance;

    public TimeProvider Time { get; } = TimeProvider.System;

    public string BasePath => _temp.Path;

    public string AppDirectory { get; }

    public string VolumeA { get; }

    public string VolumeB { get; }

    public TreeBuilder Source { get; }

    public FakeVolumes Volumes { get; }

    public AppStore Store { get; }

    public HistoryStore History { get; }

    public TargetRegistry Registry { get; }

    public TargetLocator Locator { get; }

    public JobRunner Runner { get; }

    public CleanupService Cleanup { get; }

    /// <summary>Jobs created by <see cref="JobWithTarget"/>.</summary>
    public AppConfig Config { get; set; } = new();

    public JobConfig NewJob(string name = "Photos") => new() { Name = name, SourcePath = Source.Root };

    /// <summary>Creates a job with one target at <c>volA/Backup/&lt;name&gt;</c> and adds it to <see cref="Config"/>.</summary>
    public (JobConfig Job, TargetConfig Target) JobWithTarget(string name = "Photos")
    {
        var job = NewJob(name);
        var target = Registry.AddTarget(Config, job, Path.Combine(VolumeA, "Backup", name), "External HDD");
        job = job.WithTarget(target);
        Config = Config.WithJob(job);
        return (job, target);
    }

    public string Create(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_temp.Path, relative));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose() => _temp.Dispose();
}
