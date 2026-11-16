using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Core.Paths;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.Execution;

public sealed class MirrorExecutorTests : IDisposable
{
    private static readonly DateTime T0 = TreeBuilder.BaseTime;
    private static readonly DiffOptions Insensitive = new() { CaseSensitivity = CaseSensitivity.Insensitive };
    private readonly MirrorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Mirroring_every_category_leaves_source_and_target_in_sync()
    {
        _fx.Source.File("new.txt", "new").File("new-folder/inner.txt").Folder("empty-new");
        _fx.Source.File("modified.txt", "new content", T0.AddHours(1));
        _fx.Target.File("modified.txt", "old", T0);
        _fx.Source.File("target-newer.txt", "source", T0);
        _fx.Target.File("target-newer.txt", "edited on target", T0.AddHours(1));
        _fx.Target.File("removed.txt").File("removed-folder/a.txt").File("removed-folder/sub/b.txt").Folder("removed-empty");
        _fx.Source.File("Photos/Holiday.JPG", "img");
        _fx.Target.File("photos/holiday.jpg", "img");
        _fx.Source.File("report", "now a file");
        _fx.Target.File("report/old.txt");
        _fx.Source.File("Renamed-And-Changed.txt", "new", T0.AddHours(1));
        _fx.Target.File("renamed-and-changed.txt", "older", T0);
        var diff = await _fx.AnalyzeAsync(Insensitive);

        var result = await _fx.MirrorAsync(diff, selection: diff.Items);

        MirrorFixture.AssertCompleted(result);
        Assert.Empty(result.Failures);
        Assert.Empty((await _fx.AnalyzeAsync(Insensitive)).Items);
        Assert.Empty((await _fx.AnalyzeAsync(Insensitive with { CompareMode = CompareMode.Content })).Items);
        Assert.Equal("Holiday.JPG", Path.GetFileName(Directory.GetFiles(_fx.TargetPath("Photos")).Single()));
        Assert.Equal("Photos", Path.GetFileName(Directory.GetDirectories(_fx.Target.Root, "photos").Single()));
        Assert.Equal("Renamed-And-Changed.txt", Path.GetFileName(Directory.GetFiles(_fx.Target.Root, "renamed-and-changed.txt").Single()));
        Assert.False(Directory.Exists(_fx.TargetPath(".amberkeep")));
    }

    [Fact]
    public async Task Default_selection_keeps_newer_target_files()
    {
        _fx.Source.File("a.txt", "source", T0);
        _fx.Target.File("a.txt", "newer on target", T0.AddHours(1));

        var result = await _fx.AnalyzeAndMirrorAsync();

        MirrorFixture.AssertCompleted(result);
        Assert.Equal("newer on target", File.ReadAllText(_fx.TargetPath("a.txt")));
    }

    [Fact]
    public async Task Timestamp_only_changes_update_the_time_without_copying()
    {
        _fx.Source.File("a.txt", "same", T0.AddDays(1));
        _fx.Target.File("a.txt", "same", T0);

        var result = await _fx.AnalyzeAndMirrorAsync(new DiffOptions { CompareMode = CompareMode.Content });

        Assert.Equal(1, result.UpdatedTimestamps);
        Assert.Equal(0, result.CopiedBytes);
        Assert.Equal(T0.AddDays(1), File.GetLastWriteTimeUtc(_fx.TargetPath("a.txt")));
    }

    [Fact]
    public async Task Unselected_items_are_not_touched()
    {
        _fx.Source.File("copy-me.txt").File("skip-me.txt");
        _fx.Target.File("keep-me.txt");
        var diff = await _fx.AnalyzeAsync();

        await _fx.MirrorAsync(diff, selection: diff.Items.Where(i => i.RelativePath == "copy-me.txt"));

        Assert.True(File.Exists(_fx.TargetPath("copy-me.txt")));
        Assert.False(File.Exists(_fx.TargetPath("skip-me.txt")));
        Assert.True(File.Exists(_fx.TargetPath("keep-me.txt")));
    }

    [Fact]
    public async Task Removed_folder_with_an_unselected_file_is_kept_with_a_note()
    {
        _fx.Source.File("other.txt");
        _fx.Target.File("other.txt").File("old/a.txt").File("old/keep.txt");
        var diff = await _fx.AnalyzeAsync();

        var result = await _fx.MirrorAsync(diff, selection: diff.Items.Where(i => i.RelativePath != "old/keep.txt"));

        MirrorFixture.AssertCompleted(result);
        Assert.False(File.Exists(_fx.TargetPath("old/a.txt")));
        Assert.True(File.Exists(_fx.TargetPath("old/keep.txt")));
        Assert.Contains(result.Notes, n => n.Contains("old", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Parent_folder_is_created_for_a_selected_file_even_if_unselected()
    {
        _fx.Source.File("folder/file.txt");
        var diff = await _fx.AnalyzeAsync();

        await _fx.MirrorAsync(diff, selection: diff.Items.Where(i => i.RelativePath == "folder/file.txt"));

        Assert.True(File.Exists(_fx.TargetPath("folder/file.txt")));
    }

    [Fact]
    public async Task Missing_target_folder_is_created()
    {
        _fx.Source.File("a.txt");
        var target = Path.Combine(_fx.BasePath, "new-target", "nested");
        var diff = await new DiffEngine().AnalyzeAsync(
            new DiffRequest(_fx.Source.Root, target, DiffOptions.Default), cancellationToken: TestContext.Current.CancellationToken);

        var result = await _fx.MirrorAsync(diff);

        MirrorFixture.AssertCompleted(result);
        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public async Task Archive_keeps_removed_and_overwritten_files_with_their_paths()
    {
        _fx.Source.File("docs/report.txt", "new version", T0.AddHours(1));
        _fx.Target.File("docs/report.txt", "old version", T0).File("gone/deleted.txt", "deleted content");

        var result = await _fx.AnalyzeAndMirrorAsync(options: new ExecutionOptions { Approved = true, ArchiveEnabled = true });

        MirrorFixture.AssertCompleted(result);
        Assert.Equal(2, result.ArchivedFiles);
        Assert.NotNull(result.ArchiveFolder);
        Assert.StartsWith(_fx.TargetPath(".amberkeep/archive"), result.ArchiveFolder, StringComparison.Ordinal);
        Assert.Equal("old version", File.ReadAllText(Path.Combine(result.ArchiveFolder, "docs", "report.txt")));
        Assert.Equal("deleted content", File.ReadAllText(Path.Combine(result.ArchiveFolder, "gone", "deleted.txt")));
        Assert.Equal("new version", File.ReadAllText(_fx.TargetPath("docs/report.txt")));
        Assert.Empty((await _fx.AnalyzeAsync()).Items);
        Assert.False(Directory.Exists(_fx.TargetPath(".amberkeep/tmp")));
    }

    [Fact]
    public async Task Without_archive_nothing_is_kept()
    {
        _fx.Source.File("a.txt", "new", T0.AddHours(1));
        _fx.Target.File("a.txt", "old", T0).File("gone.txt");

        var result = await _fx.AnalyzeAndMirrorAsync();

        Assert.Equal(0, result.ArchivedFiles);
        Assert.Null(result.ArchiveFolder);
        Assert.False(Directory.Exists(_fx.TargetPath(".amberkeep")));
    }

    [Fact]
    public async Task Old_archive_snapshots_are_purged_and_foreign_folders_kept()
    {
        var now = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        var old = ArchiveCleaner.SnapshotName(now.AddDays(-40));
        var recent = ArchiveCleaner.SnapshotName(now.AddDays(-5));
        _fx.Target.File($".amberkeep/archive/{old}/x.txt").File($".amberkeep/archive/{recent}/y.txt").File(".amberkeep/archive/my-notes/z.txt");
        File.SetAttributes(_fx.TargetPath($".amberkeep/archive/{old}/x.txt"), FileAttributes.ReadOnly);
        _fx.Source.File("a.txt", "new", T0.AddHours(1));
        _fx.Target.File("a.txt", "old", T0);
        var executor = new MirrorExecutor(new TestFileSystem(), new FixedTimeProvider(now));

        var result = await _fx.MirrorAsync(
            await _fx.AnalyzeAsync(),
            new ExecutionOptions { Approved = true, ArchiveEnabled = true, ArchiveRetention = TimeSpan.FromDays(30) },
            executor: executor);

        MirrorFixture.AssertCompleted(result);
        Assert.False(Directory.Exists(_fx.TargetPath($".amberkeep/archive/{old}")));
        Assert.True(Directory.Exists(_fx.TargetPath($".amberkeep/archive/{recent}")));
        Assert.True(Directory.Exists(_fx.TargetPath(".amberkeep/archive/my-notes")));
        Assert.True(Directory.Exists(result.ArchiveFolder));
    }

    [Fact]
    public void Snapshot_names_round_trip()
    {
        var now = new DateTimeOffset(2026, 3, 1, 8, 30, 15, TimeSpan.Zero);
        var name = ArchiveCleaner.SnapshotName(now);

        Assert.Equal(now, ArchiveCleaner.ParseSnapshotTime(name));
        Assert.Equal(now, ArchiveCleaner.ParseSnapshotTime(name + "-2"));
        Assert.Null(ArchiveCleaner.ParseSnapshotTime("my-notes"));
    }

    [Fact]
    public async Task Read_only_and_hidden_attributes_are_copied_and_read_only_targets_can_be_replaced()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "File attributes are Windows specific.");
        _fx.Source.File("protected.txt", "new", T0.AddHours(1));
        _fx.Target.File("protected.txt", "old", T0);
        _fx.Target.File("gone.txt");
        File.SetAttributes(_fx.Source.FullPath("protected.txt"), FileAttributes.ReadOnly | FileAttributes.Hidden);
        File.SetAttributes(_fx.TargetPath("protected.txt"), FileAttributes.ReadOnly);
        File.SetAttributes(_fx.TargetPath("gone.txt"), FileAttributes.ReadOnly);

        var result = await _fx.AnalyzeAndMirrorAsync();

        MirrorFixture.AssertCompleted(result);
        Assert.Equal("new", File.ReadAllText(_fx.TargetPath("protected.txt")));
        var attributes = File.GetAttributes(_fx.TargetPath("protected.txt"));
        Assert.True(attributes.HasFlag(FileAttributes.ReadOnly));
        Assert.True(attributes.HasFlag(FileAttributes.Hidden));
        Assert.False(File.Exists(_fx.TargetPath("gone.txt")));
    }

    [Fact]
    public async Task Long_paths_and_unicode_names_are_copied()
    {
        var segment = new string('d', 60);
        var deep = string.Join('/', Enumerable.Repeat(segment, 5)) + "/Ünïcødé Ωmega €.txt";
        _fx.Source.File(deep, "deep");

        var result = await _fx.AnalyzeAndMirrorAsync();

        MirrorFixture.AssertCompleted(result);
        Assert.Equal("deep", File.ReadAllText(_fx.TargetPath(deep)));
    }

    [Fact]
    public async Task Verification_passes_for_correct_copies()
    {
        _fx.Source.File("a.txt", new string('x', 3_000_000));

        var result = await _fx.AnalyzeAndMirrorAsync(options: new ExecutionOptions { Approved = true, VerifyCopies = true });

        MirrorFixture.AssertCompleted(result);
        Assert.Equal(3_000_000, result.CopiedBytes);
    }

    [Fact]
    public async Task Source_is_never_modified()
    {
        _fx.Source.File("a.txt", "new", T0.AddHours(1)).File("new/b.txt").Folder("empty");
        _fx.Target.File("a.txt", "old", T0).File("gone.txt");
        var before = TreeSnapshot.Take(_fx.Source.Root);

        await _fx.AnalyzeAndMirrorAsync(options: new ExecutionOptions { Approved = true, ArchiveEnabled = true, VerifyCopies = true });

        Assert.Equal(before, TreeSnapshot.Take(_fx.Source.Root));
    }

    [Fact]
    public void Write_guard_rejects_paths_outside_the_target_and_inside_the_source()
    {
        var guard = new WriteGuard(_fx.Source.Root, _fx.Target.Root);

        Assert.Equal(_fx.TargetPath("x.txt"), guard.Check(_fx.TargetPath("x.txt")));
        Assert.Throws<WriteGuardException>(() => guard.Check(_fx.Source.FullPath("x.txt")));
        Assert.Throws<WriteGuardException>(() => guard.Check(Path.Combine(_fx.BasePath, "elsewhere.txt")));
        Assert.Throws<WriteGuardException>(() => guard.Check(_fx.TargetPath("../source/x.txt")));
    }
}
