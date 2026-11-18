using System.Text;
using Amberkeep.Core.Diff;
using Amberkeep.Core.Exclusions;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Paths;
using Amberkeep.Core.Safety;
using Amberkeep.Storage.Config;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.Diff;

/// <summary>Backing up only selected folders and files of a source.</summary>
public sealed class SelectionTests : IDisposable
{
    private static readonly DateTime T0 = TreeBuilder.BaseTime;
    private readonly MirrorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Theory]
    [InlineData("Photos", true, IncludeScope.Container)]
    [InlineData("Photos", false, IncludeScope.Outside)]
    [InlineData("Photos/2026", true, IncludeScope.Included)]
    [InlineData("Photos/2026/a.jpg", false, IncludeScope.Included)]
    [InlineData("Photos/2026/deep/x", true, IncludeScope.Included)]
    [InlineData("Photos/2025", true, IncludeScope.Outside)]
    [InlineData("Photos/readme.txt", false, IncludeScope.Outside)]
    [InlineData("notes.txt", false, IncludeScope.Included)]
    [InlineData("Music", true, IncludeScope.Outside)]
    [InlineData("PHOTOS/2026/b.jpg", false, IncludeScope.Included)]
    public void Paths_are_classified_by_the_selection(string path, bool isDirectory, IncludeScope expected)
    {
        var filter = new IncludeFilter(["Photos/2026", "notes.txt"], caseSensitive: false);

        Assert.Equal(expected, filter.Classify(path, isDirectory));
    }

    [Fact]
    public void Letter_case_follows_the_backup_and_unicode_forms_always_match()
    {
        var sensitive = new IncludeFilter(["Photos"], caseSensitive: true);
        var decomposed = "Cafe/Resume".Replace("e", "e\u0301", StringComparison.Ordinal);
        var composed = decomposed.Normalize(NormalizationForm.FormC);
        var unicode = new IncludeFilter([decomposed], caseSensitive: true);

        Assert.Equal(IncludeScope.Outside, sensitive.Classify("photos/a.jpg", false));
        Assert.Equal(IncludeScope.Included, sensitive.Classify("Photos/a.jpg", false));
        Assert.Equal(IncludeScope.Included, unicode.Classify(composed + "/a.jpg", false));
    }

    [Theory]
    [InlineData("Photos", true)]
    [InlineData("Photos/2026", true)]
    [InlineData("Photos\\2026", true)]
    [InlineData("/Photos/", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("..", false)]
    [InlineData("Photos/../Music", false)]
    [InlineData("./Photos", false)]
    [InlineData("Photos//2026", false)]
    [InlineData("C:/Photos", false)]
    [InlineData("C:Photos", false)]
    [InlineData("a\0b", false)]
    public void Only_paths_inside_the_source_are_valid(string path, bool expected) =>
        Assert.Equal(expected, IncludeFilter.IsValidPath(path));

    [Fact]
    public void Compact_drops_duplicates_nested_and_invalid_paths()
    {
        var compact = IncludeFilter.Compact(["Photos/2026", "photos", "Docs\\a.txt", "Photos/2026/x", "../evil", "Docs/a.txt"], caseSensitive: false);

        // "photos" covers "Photos/2026" when letter case does not matter.
        Assert.Equal(["Docs/a.txt", "photos"], compact);
        Assert.Equal(
            ["Docs/a.txt", "Photos/2026", "photos"],
            IncludeFilter.Compact(["Photos/2026", "photos", "Docs/a.txt", "Photos/2026/x"], caseSensitive: true));
    }

    [Fact]
    public async Task Only_the_selection_is_mirrored_and_nothing_outside_it_is_touched_on_the_target()
    {
        _fx.Source.File("Photos/2026/a.jpg", "a").File("Photos/2025/old.jpg", "old").File("Photos/readme.txt", "r")
            .File("Music/song.mp3", "m").File("notes.txt", "n").File("other.txt", "o");
        _fx.Target.File("Music/kept-on-target.mp3", "t").File("Photos/2025/only-on-target.jpg", "t").File("stray.txt", "t");
        var options = new DiffOptions { IncludedPaths = ["Photos/2026", "notes.txt"] };

        var diff = await _fx.AnalyzeAsync(options);

        Assert.Equal(
            ["Photos/2026", "Photos/2026/a.jpg", "notes.txt"],
            diff.Items.Select(i => i.RelativePath).Order(StringComparer.Ordinal));
        Assert.All(diff.Items, i => Assert.Equal(DiffCategory.New, i.Category));
        Assert.Equal(2, diff.SourceFileCount);

        MirrorFixture.AssertCompleted(await _fx.MirrorAsync(diff));
        Assert.True(File.Exists(_fx.TargetPath("Photos/2026/a.jpg")));
        Assert.True(File.Exists(_fx.TargetPath("notes.txt")));
        Assert.False(File.Exists(_fx.TargetPath("Photos/readme.txt")));
        Assert.False(File.Exists(_fx.TargetPath("other.txt")));
        Assert.True(File.Exists(_fx.TargetPath("Music/kept-on-target.mp3")));
        Assert.True(File.Exists(_fx.TargetPath("Photos/2025/only-on-target.jpg")));
        Assert.True(File.Exists(_fx.TargetPath("stray.txt")));
        Assert.Empty((await _fx.AnalyzeAsync(options)).Items);
    }

    [Fact]
    public async Task Inside_the_selection_removals_and_exclusions_work_as_usual()
    {
        _fx.Source.File("Photos/a.jpg", "a").File("Photos/Thumbs.db", "thumbs");
        _fx.Target.File("Photos/a.jpg", "a").File("Photos/gone.jpg", "gone").File("Photos/Thumbs.db", "old thumbs");
        var options = new DiffOptions { IncludedPaths = ["Photos"], Exclusions = ["Thumbs.db"] };

        var diff = await _fx.AnalyzeAsync(options);

        var item = Assert.Single(diff.Items);
        Assert.Equal((DiffCategory.Removed, "Photos/gone.jpg"), (item.Category, item.RelativePath));
    }

    [Fact]
    public async Task A_missing_selected_folder_is_an_error_and_its_backup_stays()
    {
        _fx.Source.File("Documents/a.txt", "a").File("Pictures/new.jpg", "n");
        _fx.Target.File("Documents/a.txt", "a").File("Photos/1.jpg", "1").File("Photos/2.jpg", "2");
        var options = new DiffOptions { IncludedPaths = ["Documents", "Photos"] };

        var diff = await _fx.AnalyzeAsync(options);

        Assert.Empty(diff.Items);
        var error = Assert.Single(diff.Errors);
        Assert.Equal((DiffSide.Source, "Photos"), (error.Side, error.RelativePath));
        Assert.Contains("not found in the source", error.Message, StringComparison.Ordinal);
        Assert.False(diff.IsBlocked);
    }

    [Fact]
    public async Task When_every_selected_item_is_missing_the_run_stops()
    {
        _fx.Source.File("Pictures/new.jpg", "n");
        _fx.Target.File("Photos/1.jpg", "1");

        var diff = await _fx.AnalyzeAsync(new DiffOptions { IncludedPaths = ["Photos"] });

        Assert.True(diff.IsBlocked);
        Assert.Contains(diff.SafetyIssues, i => i.Kind == SafetyIssueKind.SourceEmpty);
    }

    [Fact]
    public async Task An_invalid_selection_stops_the_run_instead_of_backing_up_everything()
    {
        _fx.Source.File("a.txt");
        _fx.Target.File("b.txt");

        var diff = await _fx.AnalyzeAsync(new DiffOptions { IncludedPaths = ["../outside"] });

        Assert.True(diff.IsBlocked);
        Assert.Contains(diff.SafetyIssues, i => i.Kind == SafetyIssueKind.InvalidSelection);
        Assert.Empty(diff.Items);
    }

    [Fact]
    public async Task Links_outside_the_selection_are_not_reported()
    {
        _fx.Source.File("Photos/a.jpg", "a").Folder("real");
        Links.CreateDirectoryLinkOrSkip(_fx.Source.FullPath("link"), _fx.Source.FullPath("real"));
        try
        {
            var diff = await _fx.AnalyzeAsync(new DiffOptions { IncludedPaths = ["Photos"] });

            Assert.Empty(diff.Skipped);
        }
        finally
        {
            Links.RemoveDirectoryLink(_fx.Source.FullPath("link"));
        }
    }

    [Fact]
    public void Jobs_carry_the_selection_to_the_analysis_and_reject_invalid_paths()
    {
        var job = new JobConfig { Name = "Photos", SourcePath = _fx.Source.Root, IncludedPaths = ["2026", "../x"] };

        Assert.Equal(["2026", "../x"], job.ToDiffOptions().IncludedPaths);
        Assert.Contains(job.Validate(), e => e.Contains("../x", StringComparison.Ordinal));
        Assert.DoesNotContain((job with { IncludedPaths = ["2026"] }).Validate(), e => e.Contains("2026", StringComparison.Ordinal));
    }

    [Fact]
    public void The_selection_is_saved_and_loaded_and_a_missing_list_means_everything()
    {
        using var temp = new TempDirectory();
        var store = new Amberkeep.Storage.AppStore(temp.Path);
        var job = new JobConfig { Name = "Photos", SourcePath = _fx.Source.Root, IncludedPaths = ["2026", "Albums/Best"] };
        store.Save(new AppConfig { Jobs = [job] });

        var loaded = store.Load().Config!.Jobs.Single();
        Assert.Equal(["2026", "Albums/Best"], loaded.IncludedPaths);

        File.WriteAllText(
            Path.Combine(temp.Path, "amberkeep.json"),
            """{ "version": 1, "jobs": [ { "name": "Old", "sourcePath": "D:\\Old", "includedPaths": null } ] }""");
        Assert.Empty(store.Load().Config!.Jobs.Single().IncludedPaths);
    }
}
