using Amberkeep.App.Services;
using Amberkeep.App.ViewModels;
using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Config;

namespace Amberkeep.Tests.App;

/// <summary>Choosing the folders and files of a source to back up, in the backup editor.</summary>
public sealed class SourcePickerTests : IDisposable
{
    private readonly AppFixture _app = new();

    public SourcePickerTests()
    {
        _app.Source.File("Photos/2025/a.jpg").File("Photos/2026/b.jpg").File("Photos/readme.txt")
            .File("Music/song.mp3").File("notes.txt");
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task The_tree_lists_folders_first_and_opens_folders_lazily()
    {
        var picker = await PickerAsync([]);

        Assert.Equal(["Music", "Photos", "notes.txt"], picker.Rows.Select(r => r.Name));
        var photos = picker.Rows.Single(r => r.Name == "Photos");
        Assert.True(photos.CanExpand);
        Assert.False(photos.IsLoaded);

        await picker.SetExpandedAsync(photos, true);

        Assert.Equal(["Music", "Photos", "2025", "2026", "readme.txt", "notes.txt"], picker.Rows.Select(r => r.Name));
        await picker.SetExpandedAsync(photos, false);
        Assert.Equal(["Music", "Photos", "notes.txt"], picker.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Ticking_a_folder_selects_it_and_its_parent_shows_a_partial_state()
    {
        var picker = await PickerAsync([]);
        var photos = picker.Rows.Single(r => r.Name == "Photos");
        await picker.SetExpandedAsync(photos, true);
        var y2026 = picker.Rows.Single(r => r.Name == "2026");

        await picker.SetCheckedAsync(y2026, true);

        Assert.Equal(["Photos/2026"], picker.GetSelection());
        Assert.True(y2026.IsChecked);
        Assert.Null(photos.IsChecked);
        Assert.False(picker.Rows.Single(r => r.Name == "readme.txt").IsChecked);
        Assert.Equal("1 folder selected.", picker.SummaryText);

        // Ticking the parent replaces what was ticked inside it.
        await picker.SetCheckedAsync(photos, true);
        Assert.Equal(["Photos"], picker.GetSelection());
        Assert.True(picker.Rows.Single(r => r.Name == "readme.txt").IsChecked);
    }

    [Fact]
    public async Task Unticking_inside_a_ticked_folder_keeps_everything_else_in_it()
    {
        var picker = await PickerAsync(["Photos"]);
        var photos = picker.Rows.Single(r => r.Name == "Photos");
        await picker.SetExpandedAsync(photos, true);

        await picker.SetCheckedAsync(picker.Rows.Single(r => r.Name == "2025"), false);

        Assert.Equal(["Photos/2026", "Photos/readme.txt"], picker.GetSelection());
        Assert.Null(photos.IsChecked);
    }

    [Fact]
    public async Task Selected_folders_are_opened_and_missing_ones_are_shown_to_untick()
    {
        var picker = await PickerAsync(["Photos/2026", "Old/Gone"]);

        Assert.True(picker.Rows.Single(r => r.Name == "Photos").IsExpanded);
        var old = picker.Rows.Single(r => r.Name == "Old");
        Assert.True(old.IsMissing);
        Assert.True(old.IsExpanded);
        var gone = picker.Rows.Single(r => r.Name == "Gone");
        Assert.Equal("missing", gone.Note);
        Assert.Contains("1 selected item is missing", picker.SummaryText, StringComparison.Ordinal);

        await picker.SetCheckedAsync(gone, false);

        Assert.Equal(["Photos/2026"], picker.GetSelection());
    }

    [Fact]
    public async Task The_editor_saves_the_selection_and_shows_it_again()
    {
        var editor = new JobEditorViewModel(_app.Shell, null) { Name = "Photos", SourcePath = _app.Source.Root };
        _app.Dialogs.Folders.Enqueue(Path.Combine(_app.VolumeA, "Backup", "Photos"));
        await editor.AddTargetCommand.ExecuteAsync(null);

        editor.OnlySelected = true;
        await editor.LoadSourcePickerAsync();
        Assert.False(editor.CanSave);
        Assert.Contains(editor.Errors, e => e.StartsWith("Tick at least one", StringComparison.Ordinal));

        await editor.SourcePicker!.SetCheckedAsync(editor.SourcePicker.Rows.Single(r => r.Name == "notes.txt"), true);
        await editor.SourcePicker.SetCheckedAsync(editor.SourcePicker.Rows.Single(r => r.Name == "Music"), true);
        Assert.True(editor.CanSave);
        await editor.SaveCommand.ExecuteAsync(null);

        var saved = _app.Services.Config.Jobs.Single();
        Assert.Equal(["Music", "notes.txt"], saved.IncludedPaths);

        await _app.Shell.Dashboard.RefreshAsync();
        Assert.Equal("Only: Music, notes.txt", _app.Shell.Dashboard.Jobs.Single().SelectionText);

        var reopened = new JobEditorViewModel(_app.Shell, saved);
        Assert.True(reopened.OnlySelected);
        await reopened.LoadSourcePickerAsync();
        Assert.True(reopened.SourcePicker!.Rows.Single(r => r.Name == "Music").IsChecked);

        // Back to the whole folder.
        reopened.OnlySelected = false;
        await reopened.SaveCommand.ExecuteAsync(null);
        Assert.Empty(_app.Services.Config.Jobs.Single().IncludedPaths);
    }

    [Fact]
    public async Task Without_an_existing_source_there_is_no_tree()
    {
        var editor = new JobEditorViewModel(_app.Shell, null) { SourcePath = Path.Combine(_app.BasePath, "nowhere") };
        editor.OnlySelected = true;

        await editor.LoadSourcePickerAsync();

        Assert.Null(editor.SourcePicker);
    }

    [Fact]
    public void The_import_summary_lists_the_selection()
    {
        var job = new JobConfig { Name = "Photos", SourcePath = _app.Source.Root, IncludedPaths = ["2026", "Albums"] };

        Assert.Contains("    Only: 2026, Albums", SettingsViewModel.ImportSummary([job]), StringComparison.Ordinal);
        Assert.DoesNotContain("Only:", SettingsViewModel.ImportSummary([job with { IncludedPaths = [] }]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "A" }, "Only: A")]
    [InlineData(new[] { "A", "B", "C" }, "Only: A, B, C")]
    [InlineData(new[] { "A", "B", "C", "D", "E" }, "Only: A, B, C +2 more")]
    public void The_selection_is_shown_briefly(string[] paths, string? expected) =>
        Assert.Equal(expected, Format.Selection(paths));

    private async Task<SourcePickerViewModel> PickerAsync(IReadOnlyList<string> selected)
    {
        var picker = new SourcePickerViewModel(PhysicalFileSystem.Instance, _app.Source.Root, selected, caseSensitive: false);
        await picker.LoadAsync();
        return picker;
    }
}
