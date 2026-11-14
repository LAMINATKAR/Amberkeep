using Amberkeep.Core.Execution;
using Amberkeep.Core.FileSystem;
using Amberkeep.Storage.Archive;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.Storage;

public sealed class ArchiveBrowserTests : IDisposable
{
    private static readonly DateTime T0 = TreeBuilder.BaseTime;
    private readonly TempDirectory _temp = new();
    private readonly TreeBuilder _target;
    private readonly ArchiveBrowser _browser = new(PhysicalFileSystem.Instance);

    public ArchiveBrowserTests() => _target = new TreeBuilder(Path.Combine(_temp.Path, "target"));

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Snapshots_are_listed_newest_first_with_totals()
    {
        var older = Snapshot(DateTimeOffset.Now.AddDays(-2), ("a.txt", "12345"));
        var newer = Snapshot(DateTimeOffset.Now.AddDays(-1), ("b.txt", "1"), ("sub/c.txt", "12"));
        _target.Folder(".amberkeep/archive/not-a-snapshot");

        var snapshots = _browser.ListSnapshots(_target.Root);

        Assert.Equal([newer, older], snapshots.Select(s => s.Name));
        Assert.Equal((2, 3L), (snapshots[0].FileCount, snapshots[0].Bytes));
        Assert.Equal(["b.txt", "sub/c.txt"], _browser.ListFiles(snapshots[0]).Select(f => f.RelativePath));
    }

    [Fact]
    public void Target_without_archive_has_no_snapshots()
    {
        Assert.Empty(_browser.ListSnapshots(_target.Root));
    }

    [Fact]
    public async Task Restore_copies_files_with_their_folders_and_times_and_keeps_the_archive()
    {
        Snapshot(DateTimeOffset.Now, ("docs/report.txt", "old version"));
        var snapshot = _browser.ListSnapshots(_target.Root).Single();
        var destination = Path.Combine(_temp.Path, "restored");

        var result = await _browser.RestoreAsync(snapshot, ["docs/report.txt"], destination, overwrite: false, cancellationToken: Ct);

        Assert.Equal((1, 0), (result.Restored, result.SkippedExisting));
        var restored = Path.Combine(destination, "docs", "report.txt");
        Assert.Equal("old version", File.ReadAllText(restored));
        Assert.Equal(T0, File.GetLastWriteTimeUtc(restored));
        Assert.True(File.Exists(Path.Combine(snapshot.Path, "docs", "report.txt")));
        Assert.Single(Directory.GetFiles(Path.Combine(destination, "docs")));
    }

    [Fact]
    public async Task Existing_files_are_skipped_unless_overwrite_is_chosen()
    {
        Snapshot(DateTimeOffset.Now, ("a.txt", "archived"));
        var snapshot = _browser.ListSnapshots(_target.Root).Single();
        var destination = new TreeBuilder(Path.Combine(_temp.Path, "dest")).File("a.txt", "current");

        var skipped = await _browser.RestoreAsync(snapshot, ["a.txt"], destination.Root, overwrite: false, cancellationToken: Ct);
        Assert.Equal((0, 1), (skipped.Restored, skipped.SkippedExisting));
        Assert.Equal("current", File.ReadAllText(destination.FullPath("a.txt")));

        var replaced = await _browser.RestoreAsync(snapshot, ["a.txt"], destination.Root, overwrite: true, cancellationToken: Ct);
        Assert.Equal(1, replaced.Restored);
        Assert.Equal("archived", File.ReadAllText(destination.FullPath("a.txt")));
    }

    [Fact]
    public async Task Restoring_into_a_program_folder_is_refused()
    {
        Snapshot(DateTimeOffset.Now, ("a.txt", "x"));
        var snapshot = _browser.ListSnapshots(_target.Root).Single();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _browser.RestoreAsync(snapshot, ["a.txt"], Path.Combine(_target.Root, ".amberkeep", "x"), false, cancellationToken: Ct));
    }

    [Fact]
    public async Task Cancelled_restore_leaves_no_partial_file()
    {
        Snapshot(DateTimeOffset.Now, ("a.txt", "x"), ("b.txt", "y"));
        var snapshot = _browser.ListSnapshots(_target.Root).Single();
        var destination = Path.Combine(_temp.Path, "dest");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _browser.RestoreAsync(snapshot, ["a.txt", "b.txt"], destination, false, cancellationToken: cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(Directory.Exists(destination) && Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void Snapshots_can_be_deleted_but_other_folders_cannot()
    {
        Snapshot(DateTimeOffset.Now, ("a.txt", "x"));
        var snapshot = _browser.ListSnapshots(_target.Root).Single();

        Assert.Throws<InvalidOperationException>(() => _browser.DeleteSnapshot(snapshot with { Path = _target.Root }));
        _browser.DeleteSnapshot(snapshot);

        Assert.Empty(_browser.ListSnapshots(_target.Root));
    }

    [Fact]
    public void Old_snapshots_can_be_purged_on_demand()
    {
        var now = DateTimeOffset.Now;
        var old = Snapshot(now.AddDays(-40), ("a.txt", "x"));
        var recent = Snapshot(now.AddDays(-1), ("b.txt", "y"));

        var deleted = _browser.PurgeOld(_target.Root, TimeSpan.FromDays(30), now);

        Assert.Equal([old], deleted);
        Assert.Equal([recent], _browser.ListSnapshots(_target.Root).Select(s => s.Name));
    }

    private string Snapshot(DateTimeOffset time, params (string Path, string Content)[] files)
    {
        var name = ArchiveCleaner.SnapshotName(time);
        foreach (var (path, content) in files)
        {
            _target.File($".amberkeep/archive/{name}/{path}", content, T0);
        }

        return name;
    }
}
