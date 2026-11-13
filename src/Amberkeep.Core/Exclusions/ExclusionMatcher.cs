using System.Text;
using System.Text.RegularExpressions;
using Amberkeep.Core.Paths;

namespace Amberkeep.Core.Exclusions;

/// <summary>
/// Matches relative paths against exclusion patterns using a subset of the .gitignore syntax:
/// <list type="bullet">
/// <item><c>name/</c> – trailing slash: matches directories only.</item>
/// <item><c>*.tmp</c> – no slash: matches the name at any depth.</item>
/// <item><c>/build</c>, <c>docs/*.md</c> – contains a slash: anchored to the root of the mirrored folder.</item>
/// <item><c>*</c> and <c>?</c> never match '/', <c>**</c> matches across directories.</item>
/// <item>Empty lines and lines starting with <c>#</c> are ignored. Negation (<c>!</c>) is not supported.</item>
/// </list>
/// </summary>
public sealed class ExclusionMatcher
{
    private readonly List<Rule> _rules = [];

    public ExclusionMatcher(IEnumerable<string> patterns, bool caseSensitive)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        // Not RegexOptions.Compiled: a matcher lives for one analysis, so compiling costs more than it saves.
        var options = RegexOptions.CultureInvariant;
        if (!caseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        foreach (var raw in patterns)
        {
            var pattern = PathKey.Normalize(raw.Trim().Replace('\\', PathKey.Separator));
            if (pattern.Length == 0 || pattern.StartsWith('#'))
            {
                continue;
            }

            var directoryOnly = pattern.EndsWith(PathKey.Separator);
            pattern = pattern.TrimEnd(PathKey.Separator);
            var anchored = pattern.Contains(PathKey.Separator, StringComparison.Ordinal);
            pattern = pattern.TrimStart(PathKey.Separator);
            if (pattern.Length == 0)
            {
                continue;
            }

            _rules.Add(new Rule(new Regex(GlobToRegex(pattern), options), anchored, directoryOnly));
        }
    }

    public static ExclusionMatcher None { get; } = new([], caseSensitive: true);

    public bool IsEmpty => _rules.Count == 0;

    /// <param name="relativePath">Path relative to the mirrored folder, '/' separated.</param>
    /// <param name="isDirectory">Whether the entry is a directory.</param>
    public bool IsExcluded(string relativePath, bool isDirectory)
    {
        if (_rules.Count == 0)
        {
            return false;
        }

        // Patterns and paths are compared in Unicode form C, like names in the diff.
        relativePath = PathKey.Normalize(relativePath);
        var name = PathKey.GetName(relativePath);
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
            {
                continue;
            }

            if (rule.Regex.IsMatch(rule.Anchored ? relativePath : name))
            {
                return true;
            }
        }

        return false;
    }

    internal static string GlobToRegex(string glob)
    {
        var builder = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    var atSegmentStart = i == 0 || glob[i - 1] == PathKey.Separator;
                    var followedBySlash = i + 2 < glob.Length && glob[i + 2] == PathKey.Separator;
                    if (atSegmentStart && followedBySlash)
                    {
                        // "**/" – zero or more directories.
                        builder.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        builder.Append(".*");
                        i++;
                    }

                    break;
                case '*':
                    builder.Append("[^/]*");
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return builder.Append('$').ToString();
    }

    private sealed record Rule(Regex Regex, bool Anchored, bool DirectoryOnly);
}
