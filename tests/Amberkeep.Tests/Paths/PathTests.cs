using Amberkeep.Core.Paths;

namespace Amberkeep.Tests.Paths;

public class PathTests
{
    [Fact]
    public void Order_puts_directory_contents_right_after_the_directory()
    {
        string[] paths = ["b", "a b", "a/b", "a", "a/a"];

        var sorted = paths.Order(PathOrderComparer.Instance).ToArray();

        Assert.Equal(["a", "a/a", "a/b", "a b", "b"], sorted);
    }

    [Fact]
    public void Normalize_turns_decomposed_names_into_composed_form()
    {
        var decomposed = "café.txt";

        Assert.Equal("café.txt", PathKey.Normalize(decomposed));
    }

    [Fact]
    public void IsSelfOrUnder_checks_the_path_and_all_ancestors()
    {
        var set = new HashSet<string>(["a/b"], StringComparer.Ordinal);

        Assert.True(PathKey.IsSelfOrUnder(set, "a/b"));
        Assert.True(PathKey.IsSelfOrUnder(set, "a/b/c/d"));
        Assert.False(PathKey.IsSelfOrUnder(set, "a"));
        Assert.False(PathKey.IsSelfOrUnder(set, "a/bc"));
    }

    [Theory]
    [InlineData("data", "data", PathRelation.Same)]
    [InlineData("data", "data/backup", PathRelation.TargetInsideSource)]
    [InlineData("data/photos", "data", PathRelation.SourceInsideTarget)]
    [InlineData("data", "data2", PathRelation.Separate)]
    [InlineData("data/a", "data/b", PathRelation.Separate)]
    public void Overlap_detects_same_and_nested_folders(string source, string target, PathRelation expected)
    {
        var root = Path.GetTempPath();

        var relation = PathOverlap.Check(Path.Join(root, source), Path.Join(root, target), caseSensitive: true);

        Assert.Equal(expected, relation);
    }

    [Fact]
    public void Overlap_ignores_trailing_separators()
    {
        var root = Path.Join(Path.GetTempPath(), "data");

        Assert.Equal(PathRelation.Same, PathOverlap.Check(root + Path.DirectorySeparatorChar, root, caseSensitive: true));
    }

    [Fact]
    public void Overlap_respects_case_sensitivity()
    {
        var root = Path.GetTempPath();
        var source = Path.Join(root, "Data");
        var target = Path.Join(root, "data", "backup");

        Assert.Equal(PathRelation.TargetInsideSource, PathOverlap.Check(source, target, caseSensitive: false));
        Assert.Equal(PathRelation.Separate, PathOverlap.Check(source, target, caseSensitive: true));
    }
}
