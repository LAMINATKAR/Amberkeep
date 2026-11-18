using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Paths;
using Amberkeep.Core.Safety;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.Diff;

/// <summary>Renamed folders, moved and renamed files become one Moved item and are moved on the target, not copied.</summary>
public sealed class MoveDetectionTests : IDisposable
{
    private static readonly DateTime T0 = TreeBuilder.BaseTime;
    private readonly MirrorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task A_renamed_folder_moves_every_file_and_removes_the_old_folder()
    {
        for (var i = 0; i < 5; i++)
        {
            _fx.Source.File($"Holiday 2026/img{i}.jpg", $"photo {i}", T0.AddMinutes(i));
            _fx.Target.File($"Photos/img{i}.jpg", $"photo {i}", T0.AddMinutes(i));
        }

        _fx.Source.File("Holiday 2026/raw/big.cr3", "raw data", T0);
        _fx.Target.File("Photos/raw/big.cr3", "raw data", T0);
        var diff = await _fx.AnalyzeAsync();

        var moved = diff.Items.Where(i => i.Category == DiffCategory.Moved).ToList();
        Assert.Equal(6, moved.Count);
        Assert.Contains(moved, i => i.RelativePath == "Holiday 2026/img3.jpg" && i.Target!.RelativePath == "Photos/img3.jpg");
        Assert.DoesNotContain(diff.Items, i => i is { Kind: EntryKind.File, Category: DiffCategory.New or DiffCategory.Removed });
        Assert.Equal(0, diff.Summary.BytesToCopy);
        Assert.Equal(0, diff.Summary.BytesToRemove);
        Assert.True(diff.Summary.BytesToMove > 0);

        var result = await _fx.MirrorAsync(diff);

        MirrorFixture.AssertCompleted(result);
        Assert.Equal(6, result.MovedFiles);
        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(0, result.RemovedFiles);
        Assert.False(Directory.Exists(_fx.TargetPath("Photos")));
        Assert.Equal("photo 3", File.ReadAllText(_fx.TargetPath("Holiday 2026/img3.jpg")));
        Assert.Equal(T0.AddMinutes(3), File.GetLastWriteTimeUtc(_fx.TargetPath("Holiday 2026/img3.jpg")));
        Assert.Empty((await _fx.AnalyzeAsync()).Items);
    }

    [Fact]
    public async Task A_file_moved_to_another_folder_is_moved()
    {
        _fx.Source.File("inbox/report.pdf", "pdf", T0).File("archive/2026/report.pdf", "keep me", T0.AddDays(1));
        _fx.Target.File("inbox/report.pdf", "pdf", T0).File("drafts/plan.docx", "plan", T0);
        _fx.Source.File("done/plan.docx", "plan", T0);

        var diff = await _fx.AnalyzeAsync();

        var item = Assert.Single(diff.Items, i => i.Category == DiffCategory.Moved);
        Assert.Equal("done/plan.docx", item.RelativePath);
        Assert.Equal("drafts/plan.docx", item.Target!.RelativePath);
        Assert.Contains(diff.Items, i => i is { Category: DiffCategory.New, RelativePath: "archive/2026/report.pdf" });

        MirrorFixture.AssertCompleted(await _fx.MirrorAsync(diff));
        Assert.Equal("plan", File.ReadAllText(_fx.TargetPath("done/plan.docx")));
        Assert.Empty((await _fx.AnalyzeAsync()).Items);
    }

    [Fact]
    public async Task A_renamed_file_is_moved_only_when_its_content_is_equal()
    {
        _fx.Source.File("Beach.jpg", "beach photo", T0).File("notes-v2.txt", "abc", T0);
        _fx.Target.File("IMG_0001.jpg", "beach photo", T0).File("notes.txt", "xyz", T0);

        var diff = await _fx.AnalyzeAsync();

        var moved = Assert.Single(diff.Items, i => i.Category == DiffCategory.Moved);
        Assert.Equal("Beach.jpg", moved.RelativePath);
        Assert.Equal("IMG_0001.jpg", moved.Target!.RelativePath);

        // Same size and time, other content: copied and removed as before.
        Assert.Contains(diff.Items, i => i is { Category: DiffCategory.New, RelativePath: "notes-v2.txt" });
        Assert.Contains(diff.Items, i => i is { Category: DiffCategory.Removed, RelativePath: "notes.txt" });
    }

    [Fact]
    public async Task Ambiguous_candidates_are_not_paired()
    {
        // Two copies of the same photo moved to two folders: either could be either.
        _fx.Source.File("a/dup.jpg", "same", T0).File("b/dup.jpg", "same", T0);
        _fx.Target.File("old1/dup.jpg", "same", T0).File("old2/dup.jpg", "same", T0);

        var diff = await _fx.AnalyzeAsync();

        Assert.DoesNotContain(diff.Items, i => i.Category == DiffCategory.Moved);
        Assert.Equal(2, diff.Items.Count(i => i is { Kind: EntryKind.File, Category: DiffCategory.New }));
        Assert.Equal(2, diff.Items.Count(i => i is { Kind: EntryKind.File, Category: DiffCategory.Removed }));
    }

    [Fact]
    public async Task One_source_file_with_two_matching_old_files_is_not_paired()
    {
        _fx.Source.File("new/photo.jpg", "img", T0);
        _fx.Target.File("old1/photo.jpg", "img", T0).File("old2/photo.jpg", "img", T0);

        var diff = await _fx.AnalyzeAsync();

        Assert.DoesNotContain(diff.Items, i => i.Category == DiffCategory.Moved);
    }

    [Fact]
    public async Task Empty_files_are_not_paired()
    {
        _fx.Source.File("new/empty.txt", string.Empty, T0);
        _fx.Target.File("old/empty.txt", string.Empty, T0);

        var diff = await _fx.AnalyzeAsync();

        Assert.DoesNotContain(diff.Items, i => i.Category == DiffCategory.Moved);
    }

    [Fact]
    public async Task Size_or_time_beyond_the_tolerance_is_not_a_move()
    {
        _fx.Source.File("new/a.txt", "same", T0.AddSeconds(1)).File("new/b.txt", "same", T0.AddSeconds(10)).File("new/c.txt", "longer", T0);
        _fx.Target.File("old/a.txt", "same", T0).File("old/b.txt", "same", T0).File("old/c.txt", "short", T0);

        var diff = await _fx.AnalyzeAsync();

        var moved = Assert.Single(diff.Items, i => i.Category == DiffCategory.Moved);
        Assert.Equal("new/a.txt", moved.RelativePath);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task A_one_hour_shift_on_fat_still_pairs_when_ignored(bool ignoreShift, bool expected)
    {
        _fx.Source.File("new/a.jpg", "img", T0.AddHours(1));
        _fx.Target.File("old/a.jpg", "img", T0);
        var fat = new DiffEngine(new FaultyFileSystem { VolumeFormat = "FAT32" });

        var diff = await _fx.AnalyzeAsync(new DiffOptions { IgnoreOneHourShift = ignoreShift }, fat);

        Assert.Equal(expected, diff.Items.Any(i => i.Category == DiffCategory.Moved));
    }

    [Fact]
    public async Task Content_mode_verifies_every_pair()
    {
        _fx.Source.File("new/same.txt", "abc", T0).File("new/other.txt", "abc", T0);
        _fx.Target.File("old/same.txt", "abc", T0).File("old/other.txt", "xyz", T0);

        var diff = await _fx.AnalyzeAsync(new DiffOptions { CompareMode = CompareMode.Content });

        var moved = Assert.Single(diff.Items, i => i.Category == DiffCategory.Moved);
        Assert.Equal("new/same.txt", moved.RelativePath);
        Assert.Contains(diff.Items, i => i is { Category: DiffCategory.New, RelativePath: "new/other.txt" });
    }

    [Fact]
    public async Task Moves_into_a_folder_renamed_only_in_case_land_in_the_target_folder()
    {
        _fx.Source.File("Photos/2026/a.jpg", "a", T0).File("Photos/keep.jpg", "k", T0);
        _fx.Target.File("photos/keep.jpg", "k", T0).File("inbox/a.jpg", "a", T0);
        var options = new DiffOptions { CaseSensitivity = CaseSensitivity.Insensitive };
        var diff = await _fx.AnalyzeAsync(options);
        Assert.Contains(diff.Items, i => i is { Category: DiffCategory.CaseRename, Kind: EntryKind.Directory });
        Assert.Contains(diff.Items, i => i.Category == DiffCategory.Moved);

        // The case rename is left out: the move must still land in the existing folder, not in a second one.
        var result = await _fx.MirrorAsync(diff, selection: diff.Items.Where(i => i.Category != DiffCategory.CaseRename));

        MirrorFixture.AssertCompleted(result);
        Assert.Equal(["photos"], Directory.GetDirectories(_fx.Target.Root).Select(Path.GetFileName));
        Assert.Equal("a", File.ReadAllText(Path.Combine(_fx.Target.Root, "photos", "2026", "a.jpg")));
    }

    [Fact]
    public async Task A_case_only_difference_in_a_case_sensitive_backup_is_not_a_move()
    {
        _fx.Source.File("Holiday.JPG", "img", T0);
        _fx.Target.File("holiday.jpg", "img", T0);

        var diff = await _fx.AnalyzeAsync(new DiffOptions { CaseSensitivity = CaseSensitivity.Sensitive });

        Assert.DoesNotContain(diff.Items, i => i.Category == DiffCategory.Moved);
    }

    [Fact]
    public async Task A_file_replaced_by_a_folder_is_not_moved_into_it()
    {
        _fx.Source.File("report/report", "data", T0);
        _fx.Target.File("report", "data", T0);

        var diff = await _fx.AnalyzeAsync();

        Assert.DoesNotContain(diff.Items, i => i.Category == DiffCategory.Moved);
        MirrorFixture.AssertCompleted(await _fx.MirrorAsync(diff));
        Assert.Empty((await _fx.AnalyzeAsync()).Items);
    }

    [Fact]
    public async Task Paths_under_an_unreadable_source_folder_are_not_paired()
    {
        _fx.Source.File("private/secret.jpg", "img", T0).File("public/secret.jpg", "img", T0);
        _fx.Target.File("private/secret.jpg", "img", T0);
        var engine = new DiffEngine(new FaultyFileSystem(_fx.Source.FullPath("private")));

        var diff = await _fx.AnalyzeAsync(engine: engine);

        Assert.DoesNotContain(diff.Items, i => i.Category == DiffCategory.Moved);
        Assert.Contains(diff.Items, i => i is { Category: DiffCategory.New, RelativePath: "public/secret.jpg" });
    }

    [Fact]
    public async Task Name_collisions_are_not_paired()
    {
        _fx.Source.File("new/a.jpg", "img", T0);
        _fx.Target.File("old/a.jpg", "img", T0);
        if (!OperatingSystem.IsWindows())
        {
            _fx.Target.File("old/A.jpg", "img", T0);
        }
        else
        {
            // NTFS cannot hold both names; a case-sensitive source scan is not possible here, so test the uniqueness
            // rule instead: a second candidate with the same name makes the pair ambiguous.
            _fx.Target.File("older/a.jpg", "img", T0);
        }

        var diff = await _fx.AnalyzeAsync(new DiffOptions { CaseSensitivity = CaseSensitivity.Insensitive });

        Assert.DoesNotContain(diff.Items, i => i.Category == DiffCategory.Moved);
    }

    [Fact]
    public async Task Moves_are_not_counted_by_the_mass_removal_guard()
    {
        for (var i = 0; i < 40; i++)
        {
            _fx.Source.File($"Renamed/{i:D2}.jpg", $"photo {i}", T0.AddMinutes(i));
            _fx.Target.File($"Original/{i:D2}.jpg", $"photo {i}", T0.AddMinutes(i));
        }

        var diff = await _fx.AnalyzeAsync();

        Assert.DoesNotContain(diff.SafetyIssues, i => i.Kind == SafetyIssueKind.MassRemoval);
        Assert.False(diff.RequiresApproval);
        var result = await _fx.MirrorAsync(diff, new ExecutionOptions { Approved = false });
        MirrorFixture.AssertCompleted(result);
        Assert.Equal(40, result.MovedFiles);
    }

    [Fact]
    public async Task An_unselected_move_changes_nothing()
    {
        _fx.Source.File("new/a.jpg", "img", T0);
        _fx.Target.File("old/a.jpg", "img", T0);
        var diff = await _fx.AnalyzeAsync();

        var result = await _fx.MirrorAsync(diff, selection: diff.Items.Where(i => i.Category != DiffCategory.Moved));

        MirrorFixture.AssertCompleted(result);
        Assert.True(File.Exists(_fx.TargetPath("old/a.jpg")));
        Assert.False(File.Exists(_fx.TargetPath("new/a.jpg")));
        Assert.Contains(result.Notes, n => n.Contains("old", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_move_is_skipped_when_the_old_file_changed_on_the_target()
    {
        _fx.Source.File("new/a.jpg", "img", T0);
        _fx.Target.File("old/a.jpg", "img", T0);
        var diff = await _fx.AnalyzeAsync();
        _fx.Target.File("old/a.jpg", "edited on the target", T0.AddHours(2));

        var result = await _fx.MirrorAsync(diff);

        var failure = Assert.Single(result.Failures);
        Assert.Equal(ExecutionOperation.Move, failure.Operation);
        Assert.Equal("edited on the target", File.ReadAllText(_fx.TargetPath("old/a.jpg")));
        Assert.False(File.Exists(_fx.TargetPath("new/a.jpg")));
    }

    [Fact]
    public async Task A_move_is_skipped_when_the_source_file_changed()
    {
        _fx.Source.File("new/a.jpg", "img", T0);
        _fx.Target.File("old/a.jpg", "img", T0);
        var diff = await _fx.AnalyzeAsync();
        _fx.Source.File("new/a.jpg", "edited in the source", T0.AddHours(2));

        var result = await _fx.MirrorAsync(diff);

        Assert.Equal(ExecutionOperation.Move, Assert.Single(result.Failures).Operation);
        Assert.True(File.Exists(_fx.TargetPath("old/a.jpg")));
        Assert.False(File.Exists(_fx.TargetPath("new/a.jpg")));
    }

    [Fact]
    public async Task A_move_never_overwrites_something_that_appeared_at_the_new_place()
    {
        _fx.Source.File("new/a.jpg", "img", T0);
        _fx.Target.File("old/a.jpg", "img", T0);
        var diff = await _fx.AnalyzeAsync();
        _fx.Target.File("new/a.jpg", "put there by hand", T0);

        var result = await _fx.MirrorAsync(diff);

        Assert.Equal(ExecutionOperation.Move, Assert.Single(result.Failures).Operation);
        Assert.Equal("put there by hand", File.ReadAllText(_fx.TargetPath("new/a.jpg")));
        Assert.True(File.Exists(_fx.TargetPath("old/a.jpg")));
    }

    [Fact]
    public async Task A_move_is_skipped_when_the_old_path_is_back_in_the_source()
    {
        _fx.Source.File("new/a.jpg", "img", T0);
        _fx.Target.File("old/a.jpg", "img", T0);
        var diff = await _fx.AnalyzeAsync();
        _fx.Source.File("old/a.jpg", "img", T0);

        var result = await _fx.MirrorAsync(diff);

        Assert.Contains(result.Failures, f => f is { Operation: ExecutionOperation.Move, RelativePath: "new/a.jpg" });
        Assert.True(File.Exists(_fx.TargetPath("old/a.jpg")));
    }

    [Fact]
    public async Task A_move_is_skipped_when_a_folder_above_the_new_place_became_a_link()
    {
        _fx.Source.File("new/a.jpg", "img", T0);
        _fx.Target.File("old/a.jpg", "img", T0).Folder("new");
        var diff = await _fx.AnalyzeAsync();
        var elsewhere = Path.Combine(_fx.BasePath, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.Delete(_fx.TargetPath("new"));
        Links.CreateDirectoryLinkOrSkip(_fx.TargetPath("new"), elsewhere);

        try
        {
            var result = await _fx.MirrorAsync(diff);

            Assert.Contains(result.Failures, f => f.Operation == ExecutionOperation.Move);
            Assert.Empty(Directory.GetFiles(elsewhere));
            Assert.True(File.Exists(_fx.TargetPath("old/a.jpg")));
        }
        finally
        {
            Links.RemoveDirectoryLink(_fx.TargetPath("new"));
        }
    }

    [Fact]
    public async Task A_cancelled_run_leaves_every_file_in_one_of_its_two_places()
    {
        for (var i = 0; i < 20; i++)
        {
            _fx.Source.File($"new/{i:D2}.jpg", $"photo {i}", T0.AddMinutes(i));
            _fx.Target.File($"old/{i:D2}.jpg", $"photo {i}", T0.AddMinutes(i));
        }

        var diff = await _fx.AnalyzeAsync();
        using var cts = new CancellationTokenSource();
        var moves = 0;
        var fs = new TestFileSystem
        {
            FailMove = (_, _) =>
            {
                if (++moves == 5)
                {
                    cts.Cancel();
                }

                return false;
            },
        };

        var result = await _fx.MirrorAsync(diff, executor: new MirrorExecutor(fs), cancellationToken: cts.Token);

        Assert.Equal(ExecutionOutcome.Cancelled, result.Outcome);
        Assert.Equal(5, result.MovedFiles);
        for (var i = 0; i < 20; i++)
        {
            Assert.True(File.Exists(_fx.TargetPath($"old/{i:D2}.jpg")) ^ File.Exists(_fx.TargetPath($"new/{i:D2}.jpg")));
        }

        MirrorFixture.AssertCompleted(await _fx.AnalyzeAndMirrorAsync());
        Assert.Empty((await _fx.AnalyzeAsync()).Items);
    }

    [Fact]
    public async Task History_records_where_a_moved_file_came_from()
    {
        _fx.Source.File("new/a.jpg", "img", T0);
        _fx.Target.File("old/a.jpg", "img", T0);
        var diff = await _fx.AnalyzeAsync();
        var selected = diff.Items.Where(i => i.DefaultSelected).ToList();
        var result = await _fx.MirrorAsync(diff);

        var record = Amberkeep.Storage.History.RunRecord.Create(
            Guid.NewGuid(), Guid.NewGuid(), "Photos", Guid.NewGuid(), "Disk", diff, selected, result, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.Equal(1, record.MovedFiles);
        var change = Assert.Single(record.SelectedChanges, c => c.Category == DiffCategory.Moved);
        Assert.Equal("new/a.jpg", change.Path);
        Assert.Equal("old/a.jpg", change.From);
        Assert.Null(record.SelectedChanges.Single(c => c.Category == DiffCategory.Removed).From);
    }

    [Fact]
    public async Task A_failed_move_keeps_the_file_at_its_old_place()
    {
        _fx.Source.File("new/a.jpg", "a", T0).File("new/b.jpg", "b", T0);
        _fx.Target.File("old/a.jpg", "a", T0).File("old/b.jpg", "b", T0);
        var diff = await _fx.AnalyzeAsync();
        var fs = new TestFileSystem { FailMove = (from, _) => from.EndsWith("a.jpg", StringComparison.Ordinal) };

        var result = await _fx.MirrorAsync(diff, executor: new MirrorExecutor(fs));

        Assert.Equal(ExecutionOutcome.CompletedWithErrors, result.Outcome);
        Assert.Equal(ExecutionOperation.Move, Assert.Single(result.Failures).Operation);
        Assert.Equal(1, result.MovedFiles);
        Assert.True(File.Exists(_fx.TargetPath("old/a.jpg")));
        Assert.True(File.Exists(_fx.TargetPath("new/b.jpg")));
        Assert.Contains(result.Notes, n => n.Contains("old", StringComparison.Ordinal));
    }
}
