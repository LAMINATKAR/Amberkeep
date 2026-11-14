using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Paths;
using Amberkeep.Core.Safety;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.Safety;

/// <summary>
/// Fixes from the code review of 1.0 that protect data: aliased paths (junctions), Amberkeep's own folder,
/// removals of paths that came back, case-only replacements, Unicode forms and name collisions.
/// </summary>
public sealed class DataSafetyFixTests : IDisposable
{
    private readonly MirrorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    // --- Aliased paths -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Target_that_is_a_junction_around_the_source_is_blocked()
    {
        // data\src is the source; "alias" is a junction to data, so the source is inside the target.
        var data = Path.Combine(_fx.BasePath, "data");
        var source = new TreeBuilder(Path.Combine(data, "src")).File("a.txt").File("b.txt");
        var alias = Path.Combine(_fx.BasePath, "alias");
        Links.CreateDirectoryLinkOrSkip(alias, data);

        var diff = await new DiffEngine().AnalyzeAsync(
            new DiffRequest(source.Root, alias, DiffOptions.Default), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(diff.IsBlocked);
        Assert.Equal(SafetyIssueKind.PathsOverlap, Assert.Single(diff.SafetyIssues).Kind);
        Assert.Equal(PathRelation.SourceInsideTarget, PathOverlap.Check(source.Root, alias, caseSensitive: false, onDisk: true));
        Assert.Equal(PathRelation.Separate, PathOverlap.Check(source.Root, alias, caseSensitive: false));
    }

    [Fact]
    public async Task Executor_refuses_when_the_target_became_an_alias_of_the_source_after_the_analysis()
    {
        _fx.Source.File("keep.txt");
        _fx.Target.File("old.txt");
        var diff = await _fx.AnalyzeAsync();

        // The target folder is swapped for a junction to the parent of the source before the run.
        Directory.Delete(_fx.Target.Root, recursive: true);
        Links.CreateDirectoryLinkOrSkip(_fx.Target.Root, _fx.BasePath);

        var result = await _fx.MirrorAsync(diff, selection: diff.Items);

        Assert.Equal(ExecutionOutcome.Refused, result.Outcome);
        Assert.Contains("inside the target", result.RefusedReason, StringComparison.Ordinal);
        Assert.True(File.Exists(_fx.Source.FullPath("keep.txt")));
        Links.RemoveDirectoryLink(_fx.Target.Root);
    }

    [Fact]
    public async Task Program_folder_on_the_target_that_is_a_junction_is_refused()
    {
        _fx.Source.File("a.txt");
        var elsewhere = new TreeBuilder(Path.Combine(_fx.BasePath, "elsewhere"));
        Links.CreateDirectoryLinkOrSkip(_fx.TargetPath(".amberkeep"), elsewhere.Root);
        var diff = await _fx.AnalyzeAsync();

        var result = await _fx.MirrorAsync(diff);

        Assert.Equal(ExecutionOutcome.Refused, result.Outcome);
        Assert.Contains("link or junction", result.RefusedReason, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere.Root));
        Links.RemoveDirectoryLink(_fx.TargetPath(".amberkeep"));
    }

    [Fact]
    public void Real_path_resolves_junctions_and_keeps_the_missing_rest()
    {
        var real = new TreeBuilder(Path.Combine(_fx.BasePath, "real")).Folder("inner");
        var link = Path.Combine(_fx.BasePath, "link");
        Links.CreateDirectoryLinkOrSkip(link, real.Root);

        var resolved = PathIdentity.TryGetRealPath(Path.Combine(link, "inner", "not-yet"));

        Assert.NotNull(resolved);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(real.Root, "inner", "not-yet")),
            Path.GetFullPath(resolved),
            StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(PathIdentity.TryGetDirectoryId(real.Root), PathIdentity.TryGetDirectoryId(link));
            Assert.NotEqual(PathIdentity.TryGetDirectoryId(real.Root), PathIdentity.TryGetDirectoryId(_fx.BasePath));
        }
    }

    [Fact]
    public void Deleting_a_folder_tree_through_a_link_is_refused()
    {
        var precious = new TreeBuilder(Path.Combine(_fx.BasePath, "precious")).File("x.txt");
        var link = Path.Combine(_fx.BasePath, "link");
        Links.CreateDirectoryLinkOrSkip(link, precious.Root);

        Assert.Throws<IOException>(() => PhysicalFileSystem.Instance.DeleteDirectoryTree(link));

        Assert.True(File.Exists(precious.FullPath("x.txt")));
        Links.RemoveDirectoryLink(link);
    }

    // --- Amberkeep's own folder ----------------------------------------------------------------------------------

    [Fact]
    public async Task Target_around_the_program_folder_is_blocked()
    {
        var program = _fx.Target.Folder("Tools/Amberkeep").FullPath("Tools/Amberkeep");
        _fx.Source.File("a.txt");

        var diff = await _fx.AnalyzeAsync(new DiffOptions { ProtectedFolders = [program] });

        Assert.True(diff.IsBlocked);
        Assert.Equal(SafetyIssueKind.ProgramFolder, Assert.Single(diff.SafetyIssues).Kind);
    }

    [Fact]
    public async Task Program_folder_inside_the_source_is_skipped_on_both_sides()
    {
        var program = _fx.Source.File("Tools/Amberkeep/amberkeep.json", "{}").FullPath("Tools/Amberkeep");
        _fx.Source.File("Tools/notes.txt");
        _fx.Target.File("Tools/Amberkeep/old-copy.json");

        var diff = await _fx.AnalyzeAsync(new DiffOptions { ProtectedFolders = [program] });
        MirrorFixture.AssertCompleted(await _fx.MirrorAsync(diff));

        Assert.False(diff.IsBlocked);
        Assert.Contains(diff.SafetyIssues, i => i is { Kind: SafetyIssueKind.ProgramFolder, Severity: SafetySeverity.Info });
        Assert.True(File.Exists(_fx.TargetPath("Tools/notes.txt")));
        Assert.False(File.Exists(_fx.TargetPath("Tools/Amberkeep/amberkeep.json")));
        Assert.True(File.Exists(_fx.TargetPath("Tools/Amberkeep/old-copy.json")));
    }

    [Fact]
    public async Task Source_inside_the_program_folder_is_blocked()
    {
        var diff = await _fx.AnalyzeAsync(new DiffOptions { ProtectedFolders = [_fx.BasePath] });

        Assert.True(diff.IsBlocked);
        Assert.Contains(diff.SafetyIssues, i => i.Kind == SafetyIssueKind.ProgramFolder);
    }

    // --- Execution ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task File_put_back_into_the_source_before_the_run_is_not_removed()
    {
        _fx.Source.File("keep.txt");
        _fx.Target.File("keep.txt").File("back.txt", "backup copy").File("gone/inner.txt");
        var diff = await _fx.AnalyzeAsync();

        _fx.Source.File("back.txt", "the user restored it");
        _fx.Source.Folder("gone");
        var result = await _fx.MirrorAsync(diff);

        Assert.Equal(ExecutionOutcome.CompletedWithErrors, result.Outcome);
        Assert.Equal("backup copy", File.ReadAllText(_fx.TargetPath("back.txt")));
        Assert.Contains(result.Failures, f => f.RelativePath == "back.txt" && f.Message.Contains("source again", StringComparison.Ordinal));
        Assert.True(Directory.Exists(_fx.TargetPath("gone")));
    }

    [Fact]
    public async Task Type_change_still_replaces_a_folder_by_a_file()
    {
        _fx.Source.File("report", "now a file");
        _fx.Target.File("report/a.txt");

        MirrorFixture.AssertCompleted(await _fx.AnalyzeAndMirrorAsync());

        Assert.Equal("now a file", File.ReadAllText(_fx.TargetPath("report")));
    }

    [Fact]
    public async Task Failed_case_only_replacement_without_archive_keeps_the_old_file()
    {
        _fx.Source.File("Report.TXT", "new content");
        _fx.Target.File("report.txt", "old");
        var diff = await _fx.AnalyzeAsync();
        var fs = new TestFileSystem
        {
            // Placing the new copy fails; setting the old file aside and back must still work.
            FailMove = (from, to) => from.Contains(Path.Combine(".amberkeep", "tmp"), StringComparison.OrdinalIgnoreCase)
                                     && !from.EndsWith(".old", StringComparison.Ordinal)
                                     && !to.Contains(".amberkeep", StringComparison.OrdinalIgnoreCase),
        };

        var result = await _fx.MirrorAsync(diff, executor: new MirrorExecutor(fs, TimeProvider.System));

        Assert.Equal(ExecutionOutcome.CompletedWithErrors, result.Outcome);
        var remaining = Assert.Single(Directory.GetFiles(_fx.Target.Root));
        Assert.Equal("report.txt", Path.GetFileName(remaining));
        Assert.Equal("old", File.ReadAllText(remaining));
    }

    [Fact]
    public async Task Case_only_replacement_without_archive_leaves_one_file_with_the_new_name()
    {
        _fx.Source.File("Report.TXT", "new content");
        _fx.Target.File("report.txt", "old");

        MirrorFixture.AssertCompleted(await _fx.AnalyzeAndMirrorAsync());

        var remaining = Assert.Single(Directory.GetFiles(_fx.Target.Root));
        Assert.Equal("Report.TXT", Path.GetFileName(remaining));
        Assert.Equal("new content", File.ReadAllText(remaining));
    }

    // --- Names --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Folder_in_the_other_unicode_form_is_renamed_instead_of_duplicated()
    {
        if (OperatingSystem.IsMacOS())
        {
            return; // macOS treats both forms as one name.
        }

        const string Composed = "Café";    // NFC, typical on Windows
        const string Decomposed = "Café"; // NFD, typical from a Mac
        _fx.Source.File($"{Composed}/menu.txt", "new", TreeBuilder.BaseTime.AddDays(1)).File($"{Composed}/added.txt");
        _fx.Target.File($"{Decomposed}/menu.txt", "old");

        MirrorFixture.AssertCompleted(await _fx.AnalyzeAndMirrorAsync());

        Assert.Equal([Composed], Directory.GetDirectories(_fx.Target.Root).Select(Path.GetFileName));
        Assert.Equal("new", File.ReadAllText(_fx.TargetPath($"{Composed}/menu.txt")));
        Assert.True(File.Exists(_fx.TargetPath($"{Composed}/added.txt")));
        Assert.Empty((await _fx.AnalyzeAsync()).Items);
    }

    [Fact]
    public async Task Unticked_rename_of_a_folder_keeps_using_the_existing_folder()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        const string Composed = "Café";
        const string Decomposed = "Café";
        _fx.Source.File($"{Composed}/added.txt");
        _fx.Target.Folder(Decomposed);
        var diff = await _fx.AnalyzeAsync();

        MirrorFixture.AssertCompleted(await _fx.MirrorAsync(diff, selection: diff.Items.Where(i => i.Category != DiffCategory.CaseRename)));

        Assert.Equal([Decomposed], Directory.GetDirectories(_fx.Target.Root).Select(Path.GetFileName));
        Assert.True(File.Exists(_fx.TargetPath($"{Decomposed}/added.txt")));
    }

    [Fact]
    public async Task Names_that_differ_only_in_case_on_one_side_are_left_alone()
    {
        // As on a case-sensitive folder (WSL, git): the source lists A.txt and a.txt.
        _fx.Source.File("A.txt", "upper");
        _fx.Target.File("A.txt", "backup");
        var fs = new FaultyFileSystem();
        fs.ExtraEntries[_fx.Source.Root] = [new FileSystemEntry("a.txt", EntryKind.File, 5, TreeBuilder.BaseTime.AddDays(1), IsLink: false)];

        var diff = await _fx.AnalyzeAsync(engine: new DiffEngine(fs));

        Assert.DoesNotContain(diff.Items, i => i.RelativePath.Equals("a.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, diff.Errors.Count(e => e.Side == DiffSide.Source));
        MirrorFixture.AssertCompleted(await _fx.MirrorAsync(diff));
        Assert.Equal("backup", File.ReadAllText(_fx.TargetPath("A.txt")));
    }
}
