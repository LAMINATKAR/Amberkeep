using Amberkeep.App.ViewModels;
using Amberkeep.Core.Diff;
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

    private async Task<DiffViewModel> CreateAsync() =>
        new(_app.Shell, await _fx.AnalyzeAsync(), "Test", archiveEnabled: false, _ => Task.CompletedTask);
}
