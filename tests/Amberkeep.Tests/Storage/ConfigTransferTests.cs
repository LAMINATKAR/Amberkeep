using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;

namespace Amberkeep.Tests.Storage;

public sealed class ConfigTransferTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private string ExportPath => Path.Combine(_fx.BasePath, "export.json");

    [Fact]
    public void Exported_jobs_import_with_the_same_identities()
    {
        var (photos, target) = _fx.JobWithTarget("Photos");
        _fx.JobWithTarget("Documents");

        ConfigTransfer.Export(PhysicalFileSystem.Instance, _fx.Config, [photos.Id], ExportPath);
        var imported = ConfigTransfer.ReadImport(PhysicalFileSystem.Instance, ExportPath);

        var job = Assert.Single(imported);
        Assert.Equal(photos.Id, job.Id);
        Assert.Equal(target.Id, Assert.Single(job.Targets).Id);
        Assert.Equal(photos.Exclusions, job.Exclusions);
        Assert.DoesNotContain("historyLimit", File.ReadAllText(ExportPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Import_into_an_empty_configuration_adds_the_jobs()
    {
        var (photos, _) = _fx.JobWithTarget("Photos");
        ConfigTransfer.Export(PhysicalFileSystem.Instance, _fx.Config, [photos.Id], ExportPath);

        var result = ConfigTransfer.Merge(new AppConfig(), ConfigTransfer.ReadImport(PhysicalFileSystem.Instance, ExportPath), replaceExisting: false);

        Assert.Equal(["Photos"], result.Added);
        Assert.Equal(photos.Id, Assert.Single(result.Config.Jobs).Id);
    }

    [Fact]
    public void Existing_jobs_are_skipped_or_replaced()
    {
        var (photos, _) = _fx.JobWithTarget("Photos");
        var renamed = photos with { Name = "Photos (imported)" };

        var skipped = ConfigTransfer.Merge(_fx.Config, [renamed], replaceExisting: false);
        var replaced = ConfigTransfer.Merge(_fx.Config, [renamed], replaceExisting: true);

        Assert.Single(skipped.Skipped);
        Assert.Equal("Photos", skipped.Config.Jobs.Single().Name);
        Assert.Equal(["Photos (imported)"], replaced.Replaced);
        Assert.Equal("Photos (imported)", replaced.Config.Jobs.Single().Name);
    }

    [Fact]
    public void Job_that_conflicts_with_an_existing_target_is_skipped_with_a_reason()
    {
        var (_, target) = _fx.JobWithTarget("Photos");
        var other = new JobConfig { Name = "Other", SourcePath = target.LastKnownPath };

        var result = ConfigTransfer.Merge(_fx.Config, [other], replaceExisting: false);

        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("Other", skipped.Name);
        Assert.Contains("overlaps", skipped.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("""{ "app": "Something else", "jobs": [] }""")]
    [InlineData("""{ "app": "Amberkeep", "format": 99, "jobs": [] }""")]
    public void Unreadable_files_give_a_clear_error(string content)
    {
        File.WriteAllText(ExportPath, content);

        Assert.Throws<InvalidDataException>(() => ConfigTransfer.ReadImport(PhysicalFileSystem.Instance, ExportPath));
    }
}
