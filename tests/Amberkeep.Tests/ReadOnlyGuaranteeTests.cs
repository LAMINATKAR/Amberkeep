using Amberkeep.Core.Diff;
using Amberkeep.Tests.Infrastructure;

namespace Amberkeep.Tests;

/// <summary>The source is never written to (SPEC chapter 3), and analysis must not change anything on the source or the target.</summary>
public sealed class ReadOnlyGuaranteeTests : IDisposable
{
    private readonly MirrorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Theory]
    [InlineData(CompareMode.SizeAndTime)]
    [InlineData(CompareMode.Content)]
    public async Task Analysis_leaves_source_and_target_untouched(CompareMode mode)
    {
        var t0 = TreeBuilder.BaseTime;
        _fx.Source.File("same.txt", "same").File("changed.txt", "new", t0.AddDays(1)).File("new/a.txt").Folder("empty");
        _fx.Target.File("same.txt", "same").File("changed.txt", "old", t0).File("gone/b.txt").File(".amberkeep/target.json");
        var sourceBefore = TreeSnapshot.Take(_fx.Source.Root);
        var targetBefore = TreeSnapshot.Take(_fx.Target.Root);

        var result = await _fx.AnalyzeAsync(new DiffOptions { CompareMode = mode });

        Assert.NotEmpty(result.Items);
        Assert.Equal(sourceBefore, TreeSnapshot.Take(_fx.Source.Root));
        Assert.Equal(targetBefore, TreeSnapshot.Take(_fx.Target.Root));
    }
}
