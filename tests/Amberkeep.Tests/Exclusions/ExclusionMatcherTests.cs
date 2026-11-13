using Amberkeep.Core.Exclusions;

namespace Amberkeep.Tests.Exclusions;

public class ExclusionMatcherTests
{
    [Theory]
    // Trailing slash: directories only, at any depth.
    [InlineData("node_modules/", "node_modules", true, true)]
    [InlineData("node_modules/", "a/b/node_modules", true, true)]
    [InlineData("node_modules/", "node_modules", false, false)]
    // No slash: name at any depth.
    [InlineData("*.tmp", "x.tmp", false, true)]
    [InlineData("*.tmp", "a/b/x.tmp", false, true)]
    [InlineData("*.tmp", "x.tmp.bak", false, false)]
    [InlineData("Thumbs.db", "photos/Thumbs.db", false, true)]
    // Leading slash or inner slash: anchored to the root.
    [InlineData("/build/", "build", true, true)]
    [InlineData("/build/", "src/build", true, false)]
    [InlineData("docs/*.md", "docs/a.md", false, true)]
    [InlineData("docs/*.md", "x/docs/a.md", false, false)]
    [InlineData("docs/*.md", "docs/sub/a.md", false, false)]
    // Double star.
    [InlineData("**/cache", "cache", true, true)]
    [InlineData("**/cache", "a/b/cache", true, true)]
    [InlineData("logs/**", "logs/a/b.txt", false, true)]
    [InlineData("logs/**", "logs", true, false)]
    [InlineData("a/**/z.txt", "a/z.txt", false, true)]
    [InlineData("a/**/z.txt", "a/b/c/z.txt", false, true)]
    // Single character and literal characters.
    [InlineData("file?.txt", "file1.txt", false, true)]
    [InlineData("file?.txt", "file10.txt", false, false)]
    [InlineData("Photo [1].jpg", "Photo [1].jpg", false, true)]
    [InlineData("$RECYCLE.BIN/", "$RECYCLE.BIN", true, true)]
    // Backslashes are accepted as separators.
    [InlineData(@"build\out\", "build/out", true, true)]
    public void Matches_gitignore_style_patterns(string pattern, string path, bool isDirectory, bool expected)
    {
        var matcher = new ExclusionMatcher([pattern], caseSensitive: true);

        Assert.Equal(expected, matcher.IsExcluded(path, isDirectory));
    }

    [Fact]
    public void Ignores_comments_and_blank_lines()
    {
        var matcher = new ExclusionMatcher(["# comment", "", "   ", "/"], caseSensitive: true);

        Assert.True(matcher.IsEmpty);
        Assert.False(matcher.IsExcluded("# comment", isDirectory: false));
    }

    [Fact]
    public void Respects_case_sensitivity()
    {
        var insensitive = new ExclusionMatcher(["*.TMP"], caseSensitive: false);
        var sensitive = new ExclusionMatcher(["*.TMP"], caseSensitive: true);

        Assert.True(insensitive.IsExcluded("a/x.tmp", isDirectory: false));
        Assert.False(sensitive.IsExcluded("a/x.tmp", isDirectory: false));
    }

    [Fact]
    public void Matches_names_regardless_of_unicode_form()
    {
        var matcher = new ExclusionMatcher(["Café/"], caseSensitive: true);

        Assert.True(matcher.IsExcluded("Café", isDirectory: true));
    }

    [Fact]
    public void Default_exclusions_cover_system_folders()
    {
        var matcher = new ExclusionMatcher(DefaultExclusions.Patterns, caseSensitive: false);

        Assert.True(matcher.IsExcluded("System Volume Information", isDirectory: true));
        Assert.True(matcher.IsExcluded("$Recycle.Bin", isDirectory: true));
        Assert.True(matcher.IsExcluded("photos/.DS_Store", isDirectory: false));
        Assert.False(matcher.IsExcluded("photos/holiday.jpg", isDirectory: false));
    }
}
