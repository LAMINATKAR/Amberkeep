using Amberkeep.Core.Diff;
using Amberkeep.Core.Paths;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests.Safety;

/// <summary>
/// A folder of this computer reached through a network share (here the administrative share of the drive,
/// e.g. \\localhost\D$) is the same folder: an overlap through it must be found. Skipped where the share is
/// not accessible (it needs an administrator account).
/// </summary>
public sealed class ShareAliasTests : IDisposable
{
    private readonly MirrorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Target_through_a_share_around_the_source_is_blocked()
    {
        var data = new TreeBuilder(Path.Combine(_fx.BasePath, "data")).File("src/a.txt");
        var shared = ThroughShareOrSkip(data.Root);

        var diagnostics = $"real={PathIdentity.TryGetRealPath(shared)} id={PathIdentity.TryGetDirectoryId(shared)} localId={PathIdentity.TryGetDirectoryId(data.Root)}";
        Assert.True(
            PathOverlap.Check(data.FullPath("src"), shared, caseSensitive: false, onDisk: true) != PathRelation.Separate,
            diagnostics);

        var diff = await new DiffEngine().AnalyzeAsync(
            new DiffRequest(data.FullPath("src"), shared, DiffOptions.Default), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(diff.IsBlocked, diagnostics);
    }

    [Fact]
    public void Target_through_a_share_inside_the_source_is_blocked()
    {
        var data = new TreeBuilder(Path.Combine(_fx.BasePath, "data")).File("src/inner/a.txt");
        var shared = ThroughShareOrSkip(data.FullPath("src/inner"));

        Assert.NotEqual(PathRelation.Separate, PathOverlap.Check(data.FullPath("src"), shared, caseSensitive: false, onDisk: true));
    }

    [Fact]
    public void Unrelated_folder_through_a_share_is_allowed()
    {
        var data = new TreeBuilder(Path.Combine(_fx.BasePath, "data")).File("src/a.txt").Folder("other");
        var shared = ThroughShareOrSkip(data.FullPath("other"));

        Assert.Equal(PathRelation.Separate, PathOverlap.Check(data.FullPath("src"), shared, caseSensitive: false, onDisk: true));
    }

    // The SMB client caches folder listings for about 10 s, so a folder created a moment ago may not be visible
    // through the share yet; the test waits for it.
    private static string ThroughShareOrSkip(string localPath)
    {
        var root = Path.GetPathRoot(localPath) ?? string.Empty;
        if (!OperatingSystem.IsWindows() || root.Length != 3 || root[1] != ':')
        {
            Assert.Skip("Only on Windows with a drive letter.");
        }

        var shareRoot = @"\\localhost\" + root[0] + @"$\";
        var shared = shareRoot + localPath[3..];
        if (!Directory.Exists(shareRoot))
        {
            Assert.Skip($"The administrative share {shareRoot} is not accessible here.");
        }

        for (var i = 0; i < 40; i++)
        {
            if (Directory.Exists(shared))
            {
                return shared;
            }

            Thread.Sleep(500);
        }

        Assert.Skip($"{shared} did not become visible through the share.");
        return shared;
    }
}
