using Amberkeep.Core.Diff;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Paths;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.Diff;

public sealed class DiffEngineTests : IDisposable
{
    private static readonly DateTime T0 = TreeBuilder.BaseTime;
    private readonly MirrorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Entries_only_on_source_are_new_including_empty_directories()
    {
        _fx.Source.File("new.txt").File("folder/inner.txt").Folder("empty");

        var result = await _fx.AnalyzeAsync();

        Assert.Equal(
            [("empty", EntryKind.Directory), ("folder", EntryKind.Directory), ("folder/inner.txt", EntryKind.File), ("new.txt", EntryKind.File)],
            result.Items.Select(i => (i.RelativePath, i.Kind)));
        Assert.All(result.Items, i => Assert.Equal(DiffCategory.New, i.Category));
        Assert.All(result.Items, i => Assert.True(i.DefaultSelected));
    }

    [Fact]
    public async Task Entries_only_on_target_are_removed_item_by_item()
    {
        _fx.Source.File("keep.txt");
        _fx.Target.File("keep.txt").File("old.txt").File("old-folder/a.txt").File("old-folder/b.txt");

        var result = await _fx.AnalyzeAsync();

        Assert.Equal(["old-folder", "old-folder/a.txt", "old-folder/b.txt", "old.txt"], result.Items.Select(i => i.RelativePath));
        Assert.All(result.Items, i => Assert.Equal(DiffCategory.Removed, i.Category));
        Assert.Equal(1, result.Summary.UnchangedFiles);
    }

    [Fact]
    public async Task Identical_files_are_not_listed()
    {
        _fx.Source.File("same.txt", "abc");
        _fx.Target.File("same.txt", "abc");

        var result = await _fx.AnalyzeAsync();

        Assert.Empty(result.Items);
        Assert.Equal(1, result.Summary.UnchangedFiles);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(2000, null)]
    [InlineData(-2000, null)]
    [InlineData(2100, DiffCategory.Modified)]
    [InlineData(-2100, DiffCategory.TargetNewer)]
    public async Task Time_difference_within_two_seconds_counts_as_equal(int sourceOffsetMs, DiffCategory? expected)
    {
        _fx.Source.File("f.txt", "same", T0.AddMilliseconds(sourceOffsetMs));
        _fx.Target.File("f.txt", "same", T0);

        var result = await _fx.AnalyzeAsync();

        Assert.Equal(expected, result.Items.SingleOrDefault()?.Category);
    }

    [Theory]
    [InlineData(3600, "same", true, null)]
    [InlineData(-3601, "same", true, null)]
    [InlineData(3600, "same", false, DiffCategory.Modified)]
    [InlineData(3600, "longer", true, DiffCategory.Modified)]
    [InlineData(3603, "same", true, DiffCategory.Modified)]
    public async Task One_hour_shift_of_equal_sized_files_is_ignored_on_fat_volumes(
        int sourceOffsetSeconds, string sourceContent, bool ignoreShift, DiffCategory? expected)
    {
        _fx.Source.File("f.txt", sourceContent, T0.AddSeconds(sourceOffsetSeconds));
        _fx.Target.File("f.txt", "same", T0);
        var fat = new DiffEngine(new FaultyFileSystem { VolumeFormat = "FAT32" });

        var result = await _fx.AnalyzeAsync(new DiffOptions { IgnoreOneHourShift = ignoreShift }, fat);

        var category = result.Items.SingleOrDefault()?.Category;
        Assert.Equal(expected, category is DiffCategory.TargetNewer ? DiffCategory.Modified : category);
    }

    [Theory]
    [InlineData("NTFS")]
    [InlineData("exFAT")]
    public async Task One_hour_shift_is_a_real_change_on_utc_file_systems(string format)
    {
        _fx.Source.File("f.txt", "same", T0.AddHours(1));
        _fx.Target.File("f.txt", "same", T0);
        var engine = new DiffEngine(new FaultyFileSystem { VolumeFormat = format });

        var result = await _fx.AnalyzeAsync(DiffOptions.Default, engine);

        Assert.Equal(DiffCategory.Modified, Assert.Single(result.Items).Category);
    }

    [Fact]
    public async Task Different_size_is_modified_even_with_equal_time()
    {
        _fx.Source.File("f.txt", "longer content");
        _fx.Target.File("f.txt", "short");

        var item = Assert.Single((await _fx.AnalyzeAsync()).Items);

        Assert.Equal(DiffCategory.Modified, item.Category);
        Assert.True(item.DefaultSelected);
    }

    [Fact]
    public async Task Newer_target_is_flagged_and_not_selected()
    {
        _fx.Source.File("f.txt", "source", T0);
        _fx.Target.File("f.txt", "target edited", T0.AddHours(1));

        var item = Assert.Single((await _fx.AnalyzeAsync()).Items);

        Assert.Equal(DiffCategory.TargetNewer, item.Category);
        Assert.False(item.DefaultSelected);
    }

    [Fact]
    public async Task Case_only_difference_is_a_rename_when_names_are_case_insensitive()
    {
        _fx.Source.File("Photos/Holiday.JPG", "img");
        _fx.Target.File("photos/holiday.jpg", "img");

        var result = await _fx.AnalyzeAsync(new DiffOptions { CaseSensitivity = CaseSensitivity.Insensitive });

        Assert.Equal(
            [("Photos", DiffCategory.CaseRename), ("Photos/Holiday.JPG", DiffCategory.CaseRename)],
            result.Items.Select(i => (i.RelativePath, i.Category)));
        Assert.Equal("photos/holiday.jpg", result.Items[1].Target!.RelativePath);
    }

    [Fact]
    public async Task Case_only_difference_is_new_and_removed_when_names_are_case_sensitive()
    {
        _fx.Source.File("Holiday.JPG", "img");
        _fx.Target.File("holiday.jpg", "img");

        var result = await _fx.AnalyzeAsync(new DiffOptions { CaseSensitivity = CaseSensitivity.Sensitive });

        Assert.Equal(
            [("Holiday.JPG", DiffCategory.New), ("holiday.jpg", DiffCategory.Removed)],
            result.Items.Select(i => (i.RelativePath, i.Category)));
    }

    [Fact]
    public async Task Renamed_and_changed_file_is_one_modified_item_with_both_names()
    {
        _fx.Source.File("Report.docx", "new version", T0.AddHours(1));
        _fx.Target.File("report.docx", "old", T0);

        var item = Assert.Single((await _fx.AnalyzeAsync(new DiffOptions { CaseSensitivity = CaseSensitivity.Insensitive })).Items);

        Assert.Equal(DiffCategory.Modified, item.Category);
        Assert.Equal("Report.docx", item.Source!.RelativePath);
        Assert.Equal("report.docx", item.Target!.RelativePath);
    }

    [Fact]
    public async Task Unicode_form_difference_is_the_same_file_and_a_rename_on_windows_and_linux()
    {
        // Composed (NFC, Windows) on the source, decomposed (NFD, Mac) on the target: the same name, so not New + Removed.
        _fx.Source.File("café.txt", "x");
        _fx.Target.File("café.txt", "x");

        var result = await _fx.AnalyzeAsync();

        if (OperatingSystem.IsMacOS())
        {
            Assert.Empty(result.Items);
        }
        else
        {
            var item = Assert.Single(result.Items);
            Assert.Equal(DiffCategory.CaseRename, item.Category);
            Assert.Equal("café.txt", item.Source!.RelativePath);
        }
    }

    [Fact]
    public async Task File_replaced_by_directory_removes_old_tree_and_creates_new_entry()
    {
        _fx.Source.File("report", "now a file");
        _fx.Target.File("report/a.txt");

        var result = await _fx.AnalyzeAsync();

        Assert.Equal(
            [("report", EntryKind.Directory, DiffCategory.Removed), ("report", EntryKind.File, DiffCategory.New), ("report/a.txt", EntryKind.File, DiffCategory.Removed)],
            result.Items.Select(i => (i.RelativePath, i.Kind, i.Category)));
    }

    [Fact]
    public async Task Content_mode_detects_timestamp_only_changes()
    {
        _fx.Source.File("f.txt", "same", T0.AddDays(1));
        _fx.Target.File("f.txt", "same", T0);

        var timeMode = await _fx.AnalyzeAsync();
        var contentMode = await _fx.AnalyzeAsync(new DiffOptions { CompareMode = CompareMode.Content });

        Assert.Equal(DiffCategory.Modified, Assert.Single(timeMode.Items).Category);
        Assert.Equal(DiffCategory.TimestampOnly, Assert.Single(contentMode.Items).Category);
    }

    [Fact]
    public async Task Content_mode_detects_changes_hidden_by_equal_size_and_time()
    {
        _fx.Source.File("f.txt", "aaaa");
        _fx.Target.File("f.txt", "bbbb");

        var timeMode = await _fx.AnalyzeAsync();
        var contentMode = await _fx.AnalyzeAsync(new DiffOptions { CompareMode = CompareMode.Content });

        Assert.Empty(timeMode.Items);
        Assert.Equal(DiffCategory.Modified, Assert.Single(contentMode.Items).Category);
    }

    [Fact]
    public async Task Content_mode_reports_locked_files_as_errors()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Exclusive file locks are enforced only on Windows.");
        _fx.Source.File("locked.txt", "same");
        _fx.Target.File("locked.txt", "same");

        DiffResult result;
        using (new FileStream(_fx.Target.FullPath("locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await _fx.AnalyzeAsync(new DiffOptions { CompareMode = CompareMode.Content });
        }

        var item = Assert.Single(result.Items);
        Assert.Equal(DiffCategory.Error, item.Category);
        Assert.False(item.DefaultSelected);
        Assert.False(string.IsNullOrEmpty(item.Message));
    }

    [Fact]
    public async Task Exclusions_apply_to_both_sides_so_excluded_target_files_are_kept()
    {
        _fx.Source.File("a.txt").File("cache.tmp");
        _fx.Target.File("a.txt").File("other.tmp").File("node_modules/x.js");

        var result = await _fx.AnalyzeAsync(new DiffOptions { Exclusions = ["*.tmp", "node_modules/"] });

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Unreadable_source_folder_protects_its_backup_on_the_target()
    {
        _fx.Source.File("private/secret.txt").File("public.txt");
        _fx.Target.File("private/secret.txt").File("private/older.txt").File("public.txt");
        var engine = new DiffEngine(new FaultyFileSystem(_fx.Source.FullPath("private")));

        var result = await _fx.AnalyzeAsync(engine: engine);

        Assert.Empty(result.Items);
        var error = Assert.Single(result.Errors);
        Assert.Equal((DiffSide.Source, "private"), (error.Side, error.RelativePath));
    }

    [Fact]
    public async Task Unreadable_target_folder_receives_no_new_files()
    {
        _fx.Source.File("locked/new.txt");
        _fx.Target.File("locked/existing.txt");
        var engine = new DiffEngine(new FaultyFileSystem(_fx.Target.FullPath("locked")));

        var result = await _fx.AnalyzeAsync(engine: engine);

        Assert.Empty(result.Items);
        Assert.Equal(DiffSide.Target, Assert.Single(result.Errors).Side);
    }

    [Fact]
    public async Task Link_on_source_protects_the_same_path_on_the_target()
    {
        _fx.Source.File("real/a.txt");
        Links.CreateDirectoryLinkOrSkip(_fx.Source.FullPath("link"), _fx.Source.FullPath("real"));
        _fx.Target.File("real/a.txt").File("link/a.txt");

        var result = await _fx.AnalyzeAsync();

        Assert.Empty(result.Items);
        Assert.Equal("link", Assert.Single(result.Skipped).RelativePath);
    }

    [Fact]
    public async Task Program_folder_on_target_is_never_compared()
    {
        _fx.Source.File("a.txt");
        _fx.Target.File("a.txt").File(".amberkeep/target.json").File(".amberkeep/archive/2026/x.txt");

        var result = await _fx.AnalyzeAsync();

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Summary_totals_selected_bytes()
    {
        _fx.Source.File("new.txt", new string('n', 10));
        _fx.Source.File("changed.txt", new string('c', 20), T0.AddHours(1));
        _fx.Target.File("changed.txt", new string('o', 5), T0);
        _fx.Target.File("gone.txt", new string('g', 7));
        _fx.Source.File("older.txt", new string('s', 3), T0);
        _fx.Target.File("older.txt", new string('t', 4), T0.AddHours(1));

        var summary = (await _fx.AnalyzeAsync()).Summary;

        Assert.Equal(30, summary.BytesToCopy);
        Assert.Equal(5, summary.BytesToReplace);
        Assert.Equal(7, summary.BytesToRemove);
        Assert.Equal(30, summary.RequiredFreeSpace(archiveEnabled: true));
        Assert.Equal(23, summary.RequiredFreeSpace(archiveEnabled: false));
        Assert.Equal(new CategoryTotals(1, 0, 3), summary.Get(DiffCategory.TargetNewer));
        Assert.Equal(new CategoryTotals(1, 0, 10), summary.Get(DiffCategory.New));
    }

    [Fact]
    public async Task Progress_is_reported_until_done()
    {
        _fx.Source.File("a.txt");
        var reports = new List<AnalysisProgress>();

        await new DiffEngine().AnalyzeAsync(
            new DiffRequest(_fx.Source.Root, _fx.Target.Root, DiffOptions.Default),
            new SynchronousProgress<AnalysisProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisStage.Done, reports[^1].Stage);
        Assert.Equal(1, reports[^1].SourceEntries);
    }

    [Fact]
    public async Task Cancellation_stops_the_analysis()
    {
        _fx.Source.File("a.txt");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DiffEngine().AnalyzeAsync(
            new DiffRequest(_fx.Source.Root, _fx.Target.Root, DiffOptions.Default), cancellationToken: cts.Token));
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly Lock _lock = new();

        public void Report(T value)
        {
            lock (_lock)
            {
                handler(value);
            }
        }
    }
}
