using Amberkeep.Core.Diff;
using Amberkeep.Core.Safety;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.Safety;

public sealed class SafetyTests : IDisposable
{
    private readonly MirrorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Missing_source_blocks_the_run()
    {
        var result = await new DiffEngine().AnalyzeAsync(
            new DiffRequest(Path.Combine(_fx.BasePath, "missing"), _fx.Target.Root, DiffOptions.Default),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsBlocked);
        Assert.Equal(SafetyIssueKind.SourceMissing, Assert.Single(result.SafetyIssues).Kind);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Unreadable_source_root_blocks_the_run()
    {
        _fx.Source.File("a.txt");
        _fx.Target.File("a.txt");
        var engine = new DiffEngine(new FaultyFileSystem(_fx.Source.Root));

        var result = await _fx.AnalyzeAsync(engine: engine);

        Assert.True(result.IsBlocked);
        Assert.Equal(SafetyIssueKind.SourceUnreadable, Assert.Single(result.SafetyIssues).Kind);
    }

    [Fact]
    public async Task Unreadable_target_root_blocks_the_run()
    {
        _fx.Source.File("a.txt");
        _fx.Target.File("a.txt");
        var engine = new DiffEngine(new FaultyFileSystem(_fx.Target.Root));

        var result = await _fx.AnalyzeAsync(engine: engine);

        Assert.True(result.IsBlocked);
        Assert.Equal(SafetyIssueKind.TargetUnreadable, Assert.Single(result.SafetyIssues).Kind);
    }

    [Fact]
    public async Task Empty_source_next_to_a_filled_target_blocks_the_run()
    {
        _fx.Source.Folder("only-an-empty-folder");
        _fx.Target.File("backup.txt");

        var result = await _fx.AnalyzeAsync();

        Assert.True(result.IsBlocked);
        Assert.Contains(result.SafetyIssues, i => i.Kind == SafetyIssueKind.SourceEmpty);
    }

    [Fact]
    public async Task Empty_source_and_empty_target_is_fine()
    {
        var result = await _fx.AnalyzeAsync();

        Assert.Empty(result.SafetyIssues);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("", "backup")]
    [InlineData("", "")]
    [InlineData("backup", "")]
    public async Task Overlapping_source_and_target_block_the_run(string sourceSub, string targetSub)
    {
        _fx.Source.File("a.txt");
        var source = Path.Join(_fx.Source.Root, sourceSub);
        var target = Path.Join(_fx.Source.Root, targetSub);
        Directory.CreateDirectory(source);

        var result = await new DiffEngine().AnalyzeAsync(
            new DiffRequest(source, target, DiffOptions.Default),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsBlocked);
        Assert.Equal(SafetyIssueKind.PathsOverlap, Assert.Single(result.SafetyIssues).Kind);
    }

    [Fact]
    public async Task Overlap_uses_platform_case_rules_even_when_names_are_matched_case_sensitively()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "Needs a case-insensitive platform.");
        _fx.Source.File("a.txt");
        var target = Path.Join(_fx.Source.Root.ToUpperInvariant(), "backup");

        var result = await new DiffEngine().AnalyzeAsync(
            new DiffRequest(_fx.Source.Root, target, new DiffOptions { CaseSensitivity = Core.Paths.CaseSensitivity.Sensitive }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(SafetyIssueKind.PathsOverlap, Assert.Single(result.SafetyIssues).Kind);
    }

    [Fact]
    public async Task Target_that_is_a_file_blocks_the_run()
    {
        _fx.Source.File("a.txt");
        var target = Path.Combine(_fx.BasePath, "backup.zip");
        await File.WriteAllTextAsync(target, "zip", TestContext.Current.CancellationToken);

        var result = await new DiffEngine().AnalyzeAsync(
            new DiffRequest(_fx.Source.Root, target, DiffOptions.Default),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsBlocked);
        Assert.Equal(SafetyIssueKind.TargetIsFile, Assert.Single(result.SafetyIssues).Kind);
    }

    [Fact]
    public async Task Invalid_guard_settings_fail_before_scanning()
    {
        var engine = new DiffEngine(new FaultyFileSystem());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _fx.AnalyzeAsync(
            new DiffOptions { MassRemovalGuard = new MassRemovalGuard(Percent: 150) }, engine));
    }

    [Fact]
    public async Task Missing_target_is_info_and_everything_is_new()
    {
        _fx.Source.File("a.txt");
        var target = Path.Combine(_fx.BasePath, "not-yet-created");

        var result = await new DiffEngine().AnalyzeAsync(
            new DiffRequest(_fx.Source.Root, target, DiffOptions.Default),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsBlocked);
        Assert.Equal(SafetySeverity.Info, Assert.Single(result.SafetyIssues).Severity);
        Assert.Equal(DiffCategory.New, Assert.Single(result.Items).Category);
    }

    [Theory]
    [InlineData(19, 20, true, false)]          // 95 %, but below the 20-file floor of the share limit
    [InlineData(20, 20, true, true)]           // 100 % at the floor
    [InlineData(20, 40, true, false)]          // exactly 50 % is not more than 50 %
    [InlineData(21, 40, true, true)]           // 52.5 %
    [InlineData(999, 100_000, true, false)]    // 1 % and below the count
    [InlineData(1000, 100_000, true, true)]    // 1 %, but at the count limit
    [InlineData(40_000, 100_000, true, true)]  // a big removal in a big folder asks (it did not with AND)
    [InlineData(500, 500, false, false)]       // guard switched off
    [InlineData(0, 0, true, false)]            // empty target
    [InlineData(0, 10, true, false)]           // nothing removed
    public void Mass_removal_asks_when_either_limit_is_reached(int removed, int targetFiles, bool enabled, bool expected)
    {
        var issue = SafetyChecks.CheckMassRemoval(removed, targetFiles, new MassRemovalGuard(enabled, Percent: 50, Count: 1000));

        Assert.Equal(expected, issue is not null);
        if (issue is not null)
        {
            Assert.Equal(SafetySeverity.RequiresApproval, issue.Severity);
        }
    }

    [Fact]
    public void Mass_removal_message_names_the_limit_that_was_reached()
    {
        var guard = new MassRemovalGuard();

        Assert.Contains("more than 50 % of the files", SafetyChecks.CheckMassRemoval(30, 40, guard)!.Message, StringComparison.Ordinal);
        Assert.Contains("at least 1,000 files", SafetyChecks.CheckMassRemoval(1500, 100_000, guard)!.Message, StringComparison.Ordinal);
        Assert.StartsWith("1,500 of 100,000 files", SafetyChecks.CheckMassRemoval(1500, 100_000, guard)!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mass_removal_limits_can_be_switched_off_one_by_one()
    {
        var onlyCount = new MassRemovalGuard(Percent: 100, Count: 1000);
        Assert.Null(SafetyChecks.CheckMassRemoval(900, 900, onlyCount));
        Assert.NotNull(SafetyChecks.CheckMassRemoval(1000, 1000, onlyCount));

        var onlyShare = new MassRemovalGuard(Percent: 50, Count: 0);
        Assert.Null(SafetyChecks.CheckMassRemoval(40_000, 100_000, onlyShare));
        Assert.NotNull(SafetyChecks.CheckMassRemoval(60_000, 100_000, onlyShare));

        var neither = new MassRemovalGuard(Percent: 100, Count: 0);
        Assert.Null(SafetyChecks.CheckMassRemoval(100_000, 100_000, neither));
    }

    [Fact]
    public void Mass_removal_limits_are_configurable()
    {
        var strict = new MassRemovalGuard(Enabled: true, Percent: 10, Count: 50);

        Assert.NotNull(SafetyChecks.CheckMassRemoval(21, 200, strict));
        Assert.Null(SafetyChecks.CheckMassRemoval(19, 100, strict));
        Assert.NotNull(SafetyChecks.CheckMassRemoval(50, 10_000, strict));
    }

    [Fact]
    public void Mass_removal_rejects_a_negative_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SafetyChecks.CheckMassRemoval(1, 1, new MassRemovalGuard(Count: -1)));
    }

    [Fact]
    public void Mass_removal_rejects_invalid_percent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SafetyChecks.CheckMassRemoval(1, 1, new MassRemovalGuard(Percent: 101)));
    }

    [Fact]
    public async Task Mass_removal_requires_approval_but_does_not_block()
    {
        for (var i = 0; i < 120; i++)
        {
            _fx.Target.File($"photos/{i:D3}.jpg");
        }

        for (var i = 0; i < 10; i++)
        {
            _fx.Source.File($"photos/{i:D3}.jpg");
        }

        var result = await _fx.AnalyzeAsync();

        Assert.False(result.IsBlocked);
        Assert.True(result.RequiresApproval);
        Assert.Equal(SafetyIssueKind.MassRemoval, Assert.Single(result.SafetyIssues).Kind);
    }
}
