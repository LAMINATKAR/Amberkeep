using Amberkeep.Core.Paths;

namespace Amberkeep.Core.Execution;

/// <summary>
/// Enforces "the source is never written to" (SPEC chapter 3) in code: every write must go under the target root and never under the source root.
/// A violation is a program bug, so it throws instead of being reported as a failure.
/// </summary>
internal sealed class WriteGuard
{
    private readonly string _targetRoot;
    private readonly string? _sourceRoot;
    private readonly StringComparison _comparison;

    /// <param name="sourceRoot">Source folder that must never be written; null when there is none (archive maintenance).</param>
    /// <param name="targetRoot">The only folder that may be written.</param>
    public WriteGuard(string? sourceRoot, string targetRoot)
    {
        _comparison = CaseSensitivity.Auto.IsCaseSensitive() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _sourceRoot = sourceRoot is null ? null : WithSeparator(sourceRoot);
        _targetRoot = WithSeparator(targetRoot);
    }

    public string Check(string path)
    {
        var full = WithSeparator(path);
        if (!full.StartsWith(_targetRoot, _comparison))
        {
            throw new InvalidOperationException($"Refusing to write outside the target folder: {path}");
        }

        if (_sourceRoot is not null && full.StartsWith(_sourceRoot, _comparison))
        {
            throw new InvalidOperationException($"Refusing to write into the source folder: {path}");
        }

        return path;
    }

    private static string WithSeparator(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }
}
