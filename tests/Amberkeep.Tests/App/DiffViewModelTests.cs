using Amberkeep.App.Services;
using Amberkeep.App.ViewModels;
using Amberkeep.Core.Diff;
using Amberkeep.Core.Paths;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.App;

public sealed class DiffViewModelTests : IDisposable
{
    private static readonly DateTime T0 = TreeBuilder.BaseTime;
    private readonly AppFixture _app = new();
    private readonly MirrorFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task Tree_contains_changes_and_the_folders_that_group_them()
    {
        _fx.Source.File("a/b/new.txt").File("top.txt");
        _fx.Target.Folder("a/b");
        var vm = await CreateAsync();

        vm.ExpandAllCommand.Execute(null);

        Assert.Equal(["a", "b", "new.txt", "top.txt"], vm.Rows.Select(r => r.Name));
        Assert.Null(vm.Rows[0].Item);
        Assert.Equal(DiffCategory.New, vm.Rows[2].Category);
    }

    [Fact]
    public async Task Expanding_and_collapsing_keeps_the_same_row_collection()
    {
        _fx.Source.File("a/b/c/deep.txt").File("z.txt");
        _fx.Target.Folder("a/b/c");
        var vm = await CreateAsync();
        var rows = vm.Rows;
        var b = rows.Single(r => r.Name == "b");

        b.IsExpanded = true;
        Assert.Same(rows, vm.Rows);
        Assert.Equal(["a", "b", "c", "z.txt"], vm.Rows.Select(r => r.Name));

        vm.Rows.Single(r => r.Name == "a").IsExpanded = false;
        Assert.Same(rows, vm.Rows);
        Assert.Equal(["a", "z.txt"], vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Default_selection_skips_newer_target_files_and_folders_show_partial_state()
    {
        _fx.Source.File("docs/new.txt").File("docs/older.txt", "s", T0);
        _fx.Target.File("docs/older.txt", "target", T0.AddHours(1));
        var vm = await CreateAsync();

        var docs = vm.Rows.Single(r => r.Name == "docs");

        Assert.Null(docs.IsChecked);
        Assert.Equal(["docs/new.txt"], vm.GetSelection().Select(i => i.RelativePath));
    }

    [Fact]
    public async Task Checking_a_folder_selects_everything_below_and_unchecking_clears_it()
    {
        _fx.Source.File("docs/new.txt").File("docs/older.txt", "s", T0).File("other.txt");
        _fx.Target.File("docs/older.txt", "target", T0.AddHours(1));
        var vm = await CreateAsync();
        var docs = vm.Rows.Single(r => r.Name == "docs");

        docs.IsChecked = true;
        Assert.True(docs.IsChecked);
        Assert.Equal(3, vm.GetSelection().Count);

        docs.IsChecked = false;
        Assert.False(docs.IsChecked);
        Assert.Equal(["other.txt"], vm.GetSelection().Select(i => i.RelativePath));
        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public async Task Category_filter_and_search_limit_the_rows()
    {
        _fx.Source.File("photo.jpg").File("notes.txt");
        _fx.Target.File("old.txt");
        var vm = await CreateAsync();

        vm.Filters.Single(f => f.Category == DiffCategory.New).IsEnabled = false;
        Assert.Equal(["old.txt"], vm.Rows.Select(r => r.Name));

        vm.Filters.Single(f => f.Category == DiffCategory.New).IsEnabled = true;
        vm.SearchText = "PHOTO";
        Assert.Equal(["photo.jpg"], vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Mass_removal_warning_follows_the_selection()
    {
        for (var i = 0; i < 120; i++)
        {
            _fx.Target.File($"photos/{i:D3}.jpg");
        }

        _fx.Source.File("photos/000.jpg");
        var vm = await CreateAsync();

        Assert.NotNull(vm.MassRemovalWarning);

        vm.Rows.Single(r => r.Name == "photos").IsChecked = false;

        Assert.Null(vm.MassRemovalWarning);
    }

    [Fact]
    public async Task Blocked_diff_cannot_be_run()
    {
        _fx.Source.Folder("empty");
        _fx.Target.File("backup.txt");
        var vm = await CreateAsync();

        Assert.True(vm.IsBlocked);
        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.Contains(vm.Banners, b => b.Kind == BannerKind.Error);
    }

    [Fact]
    public async Task Run_passes_exactly_the_checked_items()
    {
        _fx.Source.File("a.txt").File("b.txt");
        IReadOnlySet<DiffItem>? passed = null;
        var diff = await _fx.AnalyzeAsync();
        var vm = new DiffViewModel(_app.Shell, diff, "t", false, s =>
        {
            passed = s;
            return Task.CompletedTask;
        });
        vm.Rows.Single(r => r.Name == "b.txt").IsChecked = false;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(["a.txt"], passed!.Select(i => i.RelativePath));
    }

    [Fact]
    public async Task Selection_totals_are_shown()
    {
        _fx.Source.File("a.txt", new string('x', 2048));
        var vm = await CreateAsync();

        Assert.Contains("1 change selected", vm.SelectionText, StringComparison.Ordinal);
        Assert.Contains("2 KB to copy", vm.SelectionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rows_are_shown_in_the_file_manager_on_the_sides_where_they_exist()
    {
        _fx.Source.File("docs/new.txt").File("docs/changed.txt", "new", T0.AddHours(1));
        _fx.Target.File("docs/changed.txt", "old", T0).File("docs/removed.txt", "removed");
        var vm = await CreateAsync();
        vm.ExpandAllCommand.Execute(null);
        var docs = vm.Rows.Single(r => r.Name == "docs");
        var added = vm.Rows.Single(r => r.Name == "new.txt");
        var changed = vm.Rows.Single(r => r.Name == "changed.txt");
        var removed = vm.Rows.Single(r => r.Name == "removed.txt");

        Assert.True(docs.ShowInSourceCommand.CanExecute(null));
        Assert.True(docs.ShowInTargetCommand.CanExecute(null));
        Assert.True(added.ShowInSourceCommand.CanExecute(null));
        Assert.False(added.ShowInTargetCommand.CanExecute(null));
        Assert.False(removed.ShowInSourceCommand.CanExecute(null));
        Assert.True(removed.ShowInTargetCommand.CanExecute(null));

        docs.ShowInSourceCommand.Execute(null);
        added.ShowInSourceCommand.Execute(null);
        changed.ShowInTargetCommand.Execute(null);
        removed.ShowInTargetCommand.Execute(null);

        Assert.Equal(
            [
                Path.Combine(_fx.Source.Root, "docs"),
                Path.Combine(_fx.Source.Root, "docs", "new.txt"),
                Path.Combine(_fx.Target.Root, "docs", "changed.txt"),
                Path.Combine(_fx.Target.Root, "docs", "removed.txt"),
            ],
            _app.Dialogs.Revealed);
    }

    [Fact]
    public async Task Case_renamed_rows_are_shown_under_each_side_own_name()
    {
        _fx.Source.File("Photos/Holiday.JPG", "img");
        _fx.Target.File("photos/holiday.jpg", "img");
        var diff = await _fx.AnalyzeAsync(new DiffOptions { CaseSensitivity = CaseSensitivity.Insensitive });
        var vm = new DiffViewModel(_app.Shell, diff, "Test", archiveEnabled: false, _ => Task.CompletedTask);
        var file = vm.Rows.Single(r => r.Name == "Holiday.JPG");

        file.ShowInSourceCommand.Execute(null);
        file.ShowInTargetCommand.Execute(null);

        Assert.Equal(
            [Path.Combine(_fx.Source.Root, "Photos", "Holiday.JPG"), Path.Combine(_fx.Target.Root, "photos", "holiday.jpg")],
            _app.Dialogs.Revealed);
    }

    [Fact]
    public void A_missing_item_falls_back_to_its_nearest_existing_folder()
    {
        var folder = Path.Combine(_fx.BasePath, "kept");
        Directory.CreateDirectory(folder);

        Assert.Equal(folder, OverlayDialogService.FindExistingParent(Path.Combine(folder, "gone", "deeper", "file.txt")));
        Assert.Equal(folder, OverlayDialogService.FindExistingParent(Path.Combine(folder, "file.txt")));
        Assert.Equal(folder, OverlayDialogService.FindExistingParent(Path.Combine(folder, "gone") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task Select_all_ticks_everything_including_newer_target_files_and_select_none_clears_it()
    {
        _fx.Source.File("docs/new.txt").File("docs/older.txt", "s", T0);
        _fx.Target.File("docs/older.txt", "target", T0.AddHours(1)).File("gone.txt", "gone");
        var vm = await CreateAsync();
        Assert.Equal(2, vm.SelectedCount);

        vm.SelectAllCommand.Execute(null);
        Assert.Equal(3, vm.SelectedCount);
        Assert.True(vm.Rows.Single(r => r.Name == "docs").IsChecked);
        Assert.Contains("gone.txt", vm.GetSelection().Select(i => i.RelativePath));

        vm.SelectNoneCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.Rows.Single(r => r.Name == "docs").IsChecked);
        Assert.False(vm.RunCommand.CanExecute(null));

        vm.SelectDefaultsCommand.Execute(null);
        Assert.Equal(2, vm.SelectedCount);
    }

    [Fact]
    public async Task Select_all_and_none_change_only_the_changes_the_filters_and_search_show()
    {
        _fx.Source.File("photo.jpg").File("notes.txt");
        _fx.Target.File("old.txt").File("older.txt");
        var vm = await CreateAsync();

        vm.Filters.Single(f => f.Category == DiffCategory.New).IsEnabled = false;
        vm.SelectNoneCommand.Execute(null);
        Assert.Equal(["notes.txt", "photo.jpg"], vm.GetSelection().Select(i => i.RelativePath).Order());

        vm.Filters.Single(f => f.Category == DiffCategory.New).IsEnabled = true;
        vm.SearchText = "older";
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(["notes.txt", "older.txt", "photo.jpg"], vm.GetSelection().Select(i => i.RelativePath).Order());

        vm.SearchText = "PHOTO";
        vm.SelectNoneCommand.Execute(null);
        Assert.Equal(["notes.txt", "older.txt"], vm.GetSelection().Select(i => i.RelativePath).Order());
    }

    [Fact]
    public async Task Select_all_recalculates_the_mass_removal_warning()
    {
        for (var i = 0; i < 30; i++)
        {
            _fx.Target.File($"photos/{i:D3}.jpg");
        }

        _fx.Source.File("keep.txt");
        var vm = await CreateAsync();
        vm.SelectNoneCommand.Execute(null);
        Assert.Null(vm.MassRemovalWarning);

        vm.SelectAllCommand.Execute(null);
        Assert.NotNull(vm.MassRemovalWarning);
    }

    [Fact]
    public async Task Files_open_on_the_sides_where_they_exist_and_folders_do_not()
    {
        _fx.Source.File("docs/new.txt").File("docs/changed.txt", "new", T0.AddHours(1));
        _fx.Target.File("docs/changed.txt", "old", T0).File("docs/removed.txt", "removed");
        var vm = await CreateAsync();
        vm.ExpandAllCommand.Execute(null);
        var docs = vm.Rows.Single(r => r.Name == "docs");
        var added = vm.Rows.Single(r => r.Name == "new.txt");
        var changed = vm.Rows.Single(r => r.Name == "changed.txt");
        var removed = vm.Rows.Single(r => r.Name == "removed.txt");

        Assert.False(docs.OpenSourceCommand.CanExecute(null));
        Assert.False(docs.OpenTargetCommand.CanExecute(null));
        Assert.True(added.OpenSourceCommand.CanExecute(null));
        Assert.False(added.OpenTargetCommand.CanExecute(null));
        Assert.False(removed.OpenSourceCommand.CanExecute(null));

        await changed.OpenTargetCommand.ExecuteAsync(null);
        await added.ActivateCommand.ExecuteAsync(null);
        await removed.ActivateCommand.ExecuteAsync(null);

        Assert.Equal(
            [
                Path.Combine(_fx.Target.Root, "docs", "changed.txt"),
                Path.Combine(_fx.Source.Root, "docs", "new.txt"),
                Path.Combine(_fx.Target.Root, "docs", "removed.txt"),
            ],
            _app.Dialogs.Opened);

        await docs.ActivateCommand.ExecuteAsync(null);
        Assert.False(docs.IsExpanded);
        Assert.Equal(3, _app.Dialogs.Opened.Count);
    }

    [Fact]
    public async Task A_case_renamed_file_opens_under_each_side_own_name()
    {
        _fx.Source.File("Photos/Holiday.JPG", "img");
        _fx.Target.File("photos/holiday.jpg", "img");
        var diff = await _fx.AnalyzeAsync(new DiffOptions { CaseSensitivity = CaseSensitivity.Insensitive });
        var vm = new DiffViewModel(_app.Shell, diff, "Test", archiveEnabled: false, _ => Task.CompletedTask);
        var file = vm.Rows.Single(r => r.Name == "Holiday.JPG");

        await file.OpenSourceCommand.ExecuteAsync(null);
        await file.OpenTargetCommand.ExecuteAsync(null);

        Assert.Equal(
            [Path.Combine(_fx.Source.Root, "Photos", "Holiday.JPG"), Path.Combine(_fx.Target.Root, "photos", "holiday.jpg")],
            _app.Dialogs.Opened);
    }

    [Fact]
    public async Task A_program_is_opened_only_after_confirmation()
    {
        _fx.Source.File("setup.exe").File("run.CMD");
        var vm = await CreateAsync();

        _app.Dialogs.Confirmations.Enqueue(false);
        await vm.Rows.Single(r => r.Name == "setup.exe").OpenSourceCommand.ExecuteAsync(null);
        Assert.Empty(_app.Dialogs.Opened);
        Assert.Equal(["Run this program?"], _app.Dialogs.Messages);

        _app.Dialogs.Confirmations.Enqueue(true);
        await vm.Rows.Single(r => r.Name == "run.CMD").OpenSourceCommand.ExecuteAsync(null);
        Assert.Equal([Path.Combine(_fx.Source.Root, "run.CMD")], _app.Dialogs.Opened);
    }

    [Fact]
    public async Task A_file_gone_since_the_analysis_is_not_opened()
    {
        _fx.Source.File("photo.jpg");
        var vm = await CreateAsync();
        File.Delete(Path.Combine(_fx.Source.Root, "photo.jpg"));

        await vm.Rows.Single(r => r.Name == "photo.jpg").OpenSourceCommand.ExecuteAsync(null);

        Assert.Empty(_app.Dialogs.Opened);
        Assert.Single(_app.Dialogs.Messages, m => m.StartsWith("File not found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_moved_file_shows_where_it_was_and_opens_there_on_the_target()
    {
        _fx.Source.File("Holiday/beach.jpg", "beach", T0);
        _fx.Target.File("Photos/beach.jpg", "beach", T0);
        var vm = await CreateAsync();
        vm.ExpandAllCommand.Execute(null);
        var row = vm.Rows.Single(r => r.Name == "beach.jpg");

        Assert.Equal(DiffCategory.Moved, row.Category);
        Assert.Equal("Moved", row.CategoryText);
        Assert.Equal("from Photos/beach.jpg", row.Detail);
        Assert.Contains("1 file moved instead of copied", vm.SelectionText, StringComparison.Ordinal);
        Assert.Contains(vm.Filters, f => f.Category == DiffCategory.Moved);

        row.ShowInTargetCommand.Execute(null);
        await row.OpenTargetCommand.ExecuteAsync(null);
        Assert.Equal([Path.Combine(_fx.Target.Root, "Photos", "beach.jpg")], _app.Dialogs.Revealed);
        Assert.Equal([Path.Combine(_fx.Target.Root, "Photos", "beach.jpg")], _app.Dialogs.Opened);
    }

    [Theory]
    [InlineData("photo.jpg", false)]
    [InlineData("report.PDF", false)]
    [InlineData("noextension", false)]
    [InlineData("setup.exe", true)]
    [InlineData("Setup.EXE", true)]
    [InlineData("script.ps1", true)]
    [InlineData("shortcut.lnk", true)]
    [InlineData("trick.exe.", true)]
    [InlineData("custom.xyz", true)]
    public void Programs_and_scripts_are_recognized(string name, bool expected) =>
        Assert.Equal(expected, FileTypes.IsProgram(Path.Combine("C:", "x", name), pathExt: ".COM;.EXE;.XYZ"));

    private async Task<DiffViewModel> CreateAsync() =>
        new(_app.Shell, await _fx.AnalyzeAsync(), "Test", archiveEnabled: false, _ => Task.CompletedTask);
}
