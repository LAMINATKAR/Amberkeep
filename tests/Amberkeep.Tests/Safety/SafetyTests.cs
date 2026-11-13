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
    [InlineData(99, 100, true, false)]   // below the minimum count
    [InlineData(100, 100, true, true)]   // 100 % and at the minimum
    [InlineData(100, 250, true, false)]  // 40 %
    [InlineData(125, 250, true, false)]  // exactly 50 % is not more than 50 %
    [InlineData(126, 250, true, true)]   // 50.4 %
    [InlineData(500, 500, false, false)] // guard switched off
    [InlineData(0, 0, true, false)]      // empty target
    public void Mass_removal_needs_both_limits_exceeded(int removed, int targetFiles, bool enabled, bool expected)
    {
        var issue = SafetyChecks.CheckMassRemoval(removed, targetFiles, new MassRemovalGuard(enabled, Percent: 50, MinCount: 100));

        Assert.Equal(expected, issue is not null);
        if (issue is not null)
        {
            Assert.Equal(SafetySeverity.RequiresApproval, issue.Severity);
        }
    }

    [Fact]
    public void Mass_removal_limits_are_configurable()
    {
        var strict = new MassRemovalGuard(Enabled: true, Percent: 10, MinCount: 5);

        Assert.NotNull(SafetyChecks.CheckMassRemoval(6, 50, strict));
        Assert.Null(SafetyChecks.CheckMassRemoval(4, 5, strict));
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
