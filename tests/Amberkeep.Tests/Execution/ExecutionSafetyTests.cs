using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.Execution;

/// <summary>Refusals, stale diffs, failures and interruption.</summary>
public sealed class ExecutionSafetyTests : IDisposable
{
    private static readonly DateTime T0 = TreeBuilder.BaseTime;
    private readonly MirrorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Blocked_diff_is_refused()
    {
        _fx.Source.Folder("empty");
        _fx.Target.File("backup.txt");
        var diff = await _fx.AnalyzeAsync();

        var result = await _fx.MirrorAsync(diff, selection: diff.Items);

        Assert.Equal(ExecutionOutcome.Refused, result.Outcome);
        Assert.True(File.Exists(_fx.TargetPath("backup.txt")));
    }

    [Fact]
    public async Task Mass_removal_without_approval_is_refused_and_changes_nothing()
    {
        FillTargetWithRemovals(120);
        var before = TreeSnapshot.Take(_fx.Target.Root);
        var diff = await _fx.AnalyzeAsync();

        var result = await _fx.MirrorAsync(diff, new ExecutionOptions { Approved = false });

        Assert.Equal(ExecutionOutcome.Refused, result.Outcome);
        Assert.Contains("approved", result.RefusedReason, StringComparison.Ordinal);
        Assert.Equal(before, TreeSnapshot.Take(_fx.Target.Root));
    }

    [Fact]
    public async Task Mass_removal_with_approval_runs()
    {
        FillTargetWithRemovals(120);

        var result = await _fx.MirrorAsync(await _fx.AnalyzeAsync(), new ExecutionOptions { Approved = true });

        MirrorFixture.AssertCompleted(result);
        Assert.Equal(110, result.RemovedFiles);
    }

    [Fact]
    public async Task Mass_removal_is_judged_on_the_selection()
    {
        FillTargetWithRemovals(120);
        var diff = await _fx.AnalyzeAsync();
        var fewRemovals = diff.Items.Where(i => i.Category != DiffCategory.Removed).Concat(
            diff.Items.Where(i => i.Category == DiffCategory.Removed).Take(5));

        var result = await _fx.MirrorAsync(diff, new ExecutionOptions { Approved = false }, fewRemovals);

        MirrorFixture.AssertCompleted(result);
        Assert.Equal(5, result.RemovedFiles);
    }

    [Fact]
    public async Task Not_enough_free_space_is_refused_and_changes_nothing()
    {
        _fx.Source.File("big.txt", new string('x', 1000));
        var executor = new MirrorExecutor(new TestFileSystem { FreeSpace = 10 });

        var result = await _fx.MirrorAsync(await _fx.AnalyzeAsync(), executor: executor);

        Assert.Equal(ExecutionOutcome.Refused, result.Outcome);
        Assert.Contains("free space", result.RefusedReason, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_fx.Target.Root));
    }

    [Fact]
    public async Task Target_file_changed_after_analysis_is_neither_removed_nor_overwritten()
    {
        _fx.Source.File("modified.txt", "source", T0.AddHours(1));
        _fx.Target.File("modified.txt", "old", T0).File("removed.txt", "old", T0);
        var diff = await _fx.AnalyzeAsync();
        _fx.Target.File("modified.txt", "edited meanwhile", T0.AddHours(2));
        _fx.Target.File("removed.txt", "edited meanwhile", T0.AddHours(2));

        var result = await _fx.MirrorAsync(diff);

        Assert.Equal(ExecutionOutcome.CompletedWithErrors, result.Outcome);
        Assert.Equal(2, result.Failures.Count);
        Assert.All(result.Failures, f => Assert.Contains("analyze again", f.Message, StringComparison.Ordinal));
        Assert.Equal("edited meanwhile", File.ReadAllText(_fx.TargetPath("modified.txt")));
        Assert.Equal("edited meanwhile", File.ReadAllText(_fx.TargetPath("removed.txt")));
    }

    [Fact]
    public async Task Source_file_changed_after_analysis_is_not_copied()
    {
        _fx.Source.File("a.txt", "v1");
        var diff = await _fx.AnalyzeAsync();
        _fx.Source.File("a.txt", "v2 longer", T0.AddHours(1));

        var result = await _fx.MirrorAsync(diff);

        Assert.Equal(ExecutionFailureCheck.Stale, Classify(Assert.Single(result.Failures)));
        Assert.False(File.Exists(_fx.TargetPath("a.txt")));
    }

    [Fact]
    public async Task File_that_appeared_on_the_target_after_analysis_is_not_overwritten()
    {
        _fx.Source.File("a.txt", "source");
        var diff = await _fx.AnalyzeAsync();
        _fx.Target.File("a.txt", "appeared meanwhile");

        var result = await _fx.MirrorAsync(diff);

        Assert.Single(result.Failures);
        Assert.Equal("appeared meanwhile", File.ReadAllText(_fx.TargetPath("a.txt")));
    }

    [Fact]
    public async Task Locked_source_file_fails_alone_and_leaves_no_temporary_files()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Exclusive file locks are enforced only on Windows.");
        _fx.Source.File("locked.txt", "locked").File("free.txt", "free");
        var diff = await _fx.AnalyzeAsync();

        ExecutionResult result;
        using (new FileStream(_fx.Source.FullPath("locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await _fx.MirrorAsync(diff);
        }

        Assert.Equal(ExecutionOutcome.CompletedWithErrors, result.Outcome);
        Assert.Equal("locked.txt", Assert.Single(result.Failures).RelativePath);
        Assert.True(File.Exists(_fx.TargetPath("free.txt")));
        Assert.False(File.Exists(_fx.TargetPath("locked.txt")));
        Assert.False(Directory.Exists(_fx.TargetPath(".amberkeep")));
    }

    [Fact]
    public async Task Cancelled_copy_leaves_no_partial_file_and_the_next_run_finishes()
    {
        _fx.Source.File("big.bin", new string('b', 5_000_000)).File("small.txt");
        using var cts = new CancellationTokenSource();
        var writes = 0;
        var fileSystem = new TestFileSystem
        {
            OnWrite = () =>
            {
                if (++writes == 2)
                {
                    cts.Cancel();
                }
            },
        };

        var cancelled = await _fx.MirrorAsync(
            await _fx.AnalyzeAsync(), executor: new MirrorExecutor(fileSystem), cancellationToken: cts.Token);

        Assert.Equal(ExecutionOutcome.Cancelled, cancelled.Outcome);
        Assert.False(File.Exists(_fx.TargetPath("big.bin")));
        Assert.False(Directory.Exists(_fx.TargetPath(".amberkeep")));

        var finished = await _fx.AnalyzeAndMirrorAsync();

        MirrorFixture.AssertCompleted(finished);
        Assert.Empty((await _fx.AnalyzeAsync()).Items);
    }

    [Fact]
    public async Task Leftover_temporary_files_from_a_crash_are_removed()
    {
        _fx.Source.File("a.txt");
        _fx.Target.File(".amberkeep/tmp/0123456789abcdef", "half written");

        var result = await _fx.AnalyzeAndMirrorAsync();

        MirrorFixture.AssertCompleted(result);
        Assert.False(Directory.Exists(_fx.TargetPath(".amberkeep/tmp")));
    }

    [Fact]
    public async Task Temporary_folder_of_a_recent_concurrent_run_is_kept()
    {
        _fx.Source.File("a.txt");
        var now = DateTimeOffset.UtcNow;
        var otherRun = now.AddMinutes(-5).ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-other";
        var oldRun = now.AddDays(-3).ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-old";
        _fx.Target.File($".amberkeep/tmp/{otherRun}/copy-in-progress").File($".amberkeep/tmp/{oldRun}/crashed");

        var result = await _fx.AnalyzeAndMirrorAsync();

        MirrorFixture.AssertCompleted(result);
        Assert.True(File.Exists(_fx.TargetPath($".amberkeep/tmp/{otherRun}/copy-in-progress")));
        Assert.False(Directory.Exists(_fx.TargetPath($".amberkeep/tmp/{oldRun}")));
    }

    [Fact]
    public async Task Unknown_free_space_is_noted()
    {
        _fx.Source.File("a.txt");
        var executor = new MirrorExecutor(new TestFileSystem { FreeSpaceUnknown = true });

        var result = await _fx.MirrorAsync(await _fx.AnalyzeAsync(), executor: executor);

        MirrorFixture.AssertCompleted(result);
        Assert.Contains(result.Notes, n => n.Contains("Free space", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_copies_do_not_count_as_written_bytes()
    {
        _fx.Source.File("a.txt", "v1");
        var diff = await _fx.AnalyzeAsync();
        _fx.Source.File("a.txt", "v2", T0.AddHours(3));

        var result = await _fx.MirrorAsync(diff);

        Assert.Equal(0, result.CopiedBytes);
    }

    private void FillTargetWithRemovals(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _fx.Target.File($"photos/{i:D3}.jpg");
        }

        for (var i = 0; i < 10; i++)
        {
            _fx.Source.File($"photos/{i:D3}.jpg");
        }
    }

    private enum ExecutionFailureCheck
    {
        Stale,
        Other,
    }

    private static ExecutionFailureCheck Classify(ExecutionFailure failure) =>
        failure.Message.Contains("analyze again", StringComparison.Ordinal) ? ExecutionFailureCheck.Stale : ExecutionFailureCheck.Other;
}
