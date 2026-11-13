using Amberkeep.Core.Exclusions;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Scanning;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.Scanning;

public sealed class ScannerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TreeBuilder _tree;

    public ScannerTests() => _tree = new TreeBuilder(Path.Combine(_temp.Path, "root"));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Lists_files_and_directories_recursively_including_empty_ones()
    {
        _tree.File("a.txt", "12345").File("docs/b.txt").Folder("empty");

        var result = Scan();

        Assert.True(result.RootExists);
        Assert.Empty(result.Errors);
        var entries = result.Entries.ToDictionary(e => e.RelativePath);
        Assert.Equal(["a.txt", "docs", "docs/b.txt", "empty"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(EntryKind.File, entries["a.txt"].Kind);
        Assert.Equal(5, entries["a.txt"].Length);
        Assert.Equal(TreeBuilder.BaseTime, entries["a.txt"].LastWriteTimeUtc);
        Assert.Equal(EntryKind.Directory, entries["empty"].Kind);
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public void Missing_root_is_reported_not_thrown()
    {
        var result = new Scanner(PhysicalFileSystem.Instance).Scan(
            Path.Combine(_temp.Path, "missing"), ExclusionMatcher.None, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.RootExists);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Excluded_directories_are_never_opened()
    {
        _tree.File("node_modules/lib/x.js").File("app.js");
        var fileSystem = new FaultyFileSystem(_tree.FullPath("node_modules"));

        var result = new Scanner(fileSystem).Scan(
            _tree.Root,
            new ExclusionMatcher(["node_modules/"], caseSensitive: true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
        Assert.Equal(["app.js"], result.Entries.Select(e => e.RelativePath));
        Assert.DoesNotContain(fileSystem.ListedDirectories, d => d.EndsWith("node_modules", StringComparison.Ordinal));
    }

    [Fact]
    public void Program_folder_is_skipped_only_in_the_root()
    {
        _tree.File(".amberkeep/target.json").File("sub/.amberkeep/data.txt");

        var result = Scan();

        Assert.DoesNotContain(result.Entries, e => e.RelativePath.StartsWith(".amberkeep", StringComparison.Ordinal));
        Assert.Contains(result.Entries, e => e.RelativePath == "sub/.amberkeep/data.txt");
    }

    [Fact]
    public void Unreadable_directory_is_reported_and_scan_continues()
    {
        _tree.File("private/secret.txt").File("public/readme.txt");
        var fileSystem = new FaultyFileSystem(_tree.FullPath("private"));

        var result = new Scanner(fileSystem).Scan(
            _tree.Root, ExclusionMatcher.None, cancellationToken: TestContext.Current.CancellationToken);

        var error = Assert.Single(result.Errors);
        Assert.Equal("private", error.RelativePath);
        Assert.Contains(result.Entries, e => e.RelativePath == "public/readme.txt");
    }

    [Fact]
    public void Unreadable_root_is_reported_as_root_error()
    {
        _tree.File("a.txt");
        var fileSystem = new FaultyFileSystem(_tree.Root);

        var result = new Scanner(fileSystem).Scan(
            _tree.Root, ExclusionMatcher.None, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.RootExists);
        Assert.NotNull(result.RootError);
    }

    [Fact]
    public void Symbolic_links_are_skipped_and_not_followed()
    {
        _tree.File("real/file.txt");
        Links.CreateDirectoryLinkOrSkip(_tree.FullPath("link"), _tree.FullPath("real"));

        var result = Scan();

        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("link", skipped.RelativePath);
        Assert.DoesNotContain(result.Entries, e => e.RelativePath.StartsWith("link", StringComparison.Ordinal));
    }

    [Fact]
    public void Paths_longer_than_260_characters_are_scanned()
    {
        var segment = new string('d', 60);
        var relative = string.Join('/', Enumerable.Repeat(segment, 5)) + "/deep.txt";
        _tree.File(relative);

        var result = Scan();

        Assert.True(_tree.FullPath(relative).Length > 260);
        Assert.Contains(result.Entries, e => e.RelativePath == relative);
    }

    [Fact]
    public void Hidden_and_system_files_are_included()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Hidden/System attributes are Windows specific.");
        _tree.File("hidden.txt");
        File.SetAttributes(_tree.FullPath("hidden.txt"), FileAttributes.Hidden | FileAttributes.System);

        var result = Scan();

        Assert.Contains(result.Entries, e => e.RelativePath == "hidden.txt");
    }

    [Fact]
    public void Unicode_names_are_kept_as_stored()
    {
        _tree.File("Ünïcødé/Ωmega €.txt");

        var result = Scan();

        Assert.Contains(result.Entries, e => e.RelativePath == "Ünïcødé/Ωmega €.txt");
    }

    [Fact]
    public void Cancellation_stops_the_scan()
    {
        _tree.File("a.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new Scanner(PhysicalFileSystem.Instance).Scan(_tree.Root, ExclusionMatcher.None, cancellationToken: cts.Token));
    }

    private ScanResult Scan() => new Scanner(PhysicalFileSystem.Instance).Scan(
        _tree.Root, ExclusionMatcher.None, cancellationToken: TestContext.Current.CancellationToken);
}
