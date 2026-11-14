using Amberkeep.Core.Diff;
using Amberkeep.Core.Safety;
using Amberkeep.Storage.Config;

namespace Amberkeep.Tests.Storage;

public class JobConfigTests
{
    private static readonly string Root = Path.GetTempPath();

    [Fact]
    public void Maps_to_diff_and_execution_options()
    {
        var job = new JobConfig
        {
            Name = "Photos",
            SourcePath = Path.Join(Root, "photos"),
            CompareMode = CompareMode.Content,
            IgnoreOneHourShift = false,
            Exclusions = ["*.tmp"],
            MassRemovalGuard = new MassRemovalGuard(false),
            Archive = new ArchiveSettings { Enabled = true, RetentionDays = 7 },
            VerifyCopies = true,
        };

        var diff = job.ToDiffOptions();
        var execution = job.ToExecutionOptions(approved: true);

        Assert.Equal(CompareMode.Content, diff.CompareMode);
        Assert.False(diff.IgnoreOneHourShift);
        Assert.Equal(["*.tmp"], diff.Exclusions);
        Assert.False(diff.MassRemovalGuard.Enabled);
        Assert.True(execution.ArchiveEnabled);
        Assert.Equal(TimeSpan.FromDays(7), execution.ArchiveRetention);
        Assert.True(execution.Approved);
        Assert.True(execution.VerifyCopies);
    }

    [Fact]
    public void Archive_without_retention_keeps_forever()
    {
        var job = new JobConfig { Archive = new ArchiveSettings { Enabled = true, RetentionDays = null } };

        Assert.Null(job.ToExecutionOptions(approved: false).ArchiveRetention);
    }

    [Fact]
    public void New_job_starts_with_default_exclusions_and_safe_defaults()
    {
        var job = new JobConfig();

        Assert.Contains("System Volume Information/", job.Exclusions);
        Assert.True(job.RequireApproval);
        Assert.False(job.Archive.Enabled);
        Assert.Equal(30, job.Archive.RetentionDays);
        Assert.False(job.Trigger.IsAutomatic);
    }

    [Fact]
    public void Valid_job_has_no_errors()
    {
        var job = new JobConfig { Name = "Photos", SourcePath = Path.Join(Root, "photos") }
            .WithTarget(new TargetConfig { Name = "HDD", LastKnownPath = Path.Join(Root, "backup") });

        Assert.Empty(job.Validate());
    }

    [Fact]
    public void Validation_reports_every_problem()
    {
        var job = new JobConfig
        {
            Name = " ",
            SourcePath = "relative/path",
            MassRemovalGuard = new MassRemovalGuard(Percent: 150),
            Archive = new ArchiveSettings { RetentionDays = 0 },
        };

        Assert.Equal(4, job.Validate().Count);
    }

    [Fact]
    public void Target_inside_the_source_is_invalid()
    {
        var source = Path.Join(Root, "photos");
        var job = new JobConfig { Name = "Photos", SourcePath = source }
            .WithTarget(new TargetConfig { Name = "Inside", LastKnownPath = Path.Join(source, "backup") });

        Assert.Contains(job.Validate(), e => e.Contains("inside", StringComparison.Ordinal));
    }

    [Fact]
    public void WithJob_replaces_by_id_and_WithoutJob_removes()
    {
        var job = new JobConfig { Name = "A" };
        var config = new AppConfig().WithJob(job).WithJob(job with { Name = "B" });

        Assert.Equal("B", Assert.Single(config.Jobs).Name);
        Assert.Empty(config.WithoutJob(job.Id).Jobs);
    }
}
