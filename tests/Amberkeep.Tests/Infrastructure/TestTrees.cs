using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;

namespace Amberkeep.Tests.Infrastructure;

/// <summary>
/// A unique folder under the system temp directory (or under AMBERKEEP_TEST_TEMP when set), deleted on dispose.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        var root = Environment.GetEnvironmentVariable("AMBERKEEP_TEST_TEMP") is { Length: > 0 } custom
            ? custom
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amberkeep-tests");
        Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         AttributesToSkip = 0,
                         IgnoreInaccessible = true,
                     }))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; leftovers stay in the temp folder.
        }
    }
}

/// <summary>Fluent builder for files and folders under a root.</summary>
internal sealed class TreeBuilder
{
    public static readonly DateTime BaseTime = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    public TreeBuilder(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    public TreeBuilder File(string relativePath, string content = "content", DateTime? modifiedUtc = null)
    {
        var full = FullPath(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        System.IO.File.SetLastWriteTimeUtc(full, modifiedUtc ?? BaseTime);
        return this;
    }

    public TreeBuilder Folder(string relativePath)
    {
        Directory.CreateDirectory(FullPath(relativePath));
        return this;
    }

    public string FullPath(string relativePath) =>
        System.IO.Path.Join(Root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
}

/// <summary>Source and target folders side by side in one temp directory.</summary>
internal sealed class MirrorFixture : IDisposable
{
    private readonly TempDirectory _temp = new();

    public MirrorFixture()
    {
        Source = new TreeBuilder(System.IO.Path.Combine(_temp.Path, "source"));
        Target = new TreeBuilder(System.IO.Path.Combine(_temp.Path, "target"));
    }

    public string BasePath => _temp.Path;

    public TreeBuilder Source { get; }

    public TreeBuilder Target { get; }

    public Task<DiffResult> AnalyzeAsync(DiffOptions? options = null, DiffEngine? engine = null) =>
        (engine ?? new DiffEngine()).AnalyzeAsync(
            new DiffRequest(Source.Root, Target.Root, options ?? DiffOptions.Default),
            cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Applies <paramref name="diff"/>; by default the default selection with approval.</summary>
    public Task<ExecutionResult> MirrorAsync(
        DiffResult diff,
        ExecutionOptions? options = null,
        IEnumerable<DiffItem>? selection = null,
        MirrorExecutor? executor = null,
        CancellationToken? cancellationToken = null) =>
        (executor ?? new MirrorExecutor()).ExecuteAsync(
            diff,
            (selection ?? diff.Items.Where(i => i.DefaultSelected)).ToHashSet(),
            options ?? new ExecutionOptions { Approved = true },
            cancellationToken: cancellationToken ?? TestContext.Current.CancellationToken);

    /// <summary>Analyzes and applies the default selection.</summary>
    public async Task<ExecutionResult> AnalyzeAndMirrorAsync(DiffOptions? diffOptions = null, ExecutionOptions? options = null) =>
        await MirrorAsync(await AnalyzeAsync(diffOptions), options);

    public string TargetPath(string relativePath) => Target.FullPath(relativePath);

    public static void AssertCompleted(ExecutionResult result) => Assert.True(
        result.Outcome == ExecutionOutcome.Completed,
        $"{result.Outcome}: {result.RefusedReason} {string.Join("; ", result.Failures.Select(f => $"{f.Operation} {f.RelativePath}: {f.Message}"))}");

    public void Dispose() => _temp.Dispose();
}
