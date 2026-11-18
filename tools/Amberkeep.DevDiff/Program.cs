// Development tool: prints the diff Amberkeep would make and can apply it. Not shipped.
using System.Globalization;
using Amberkeep.Core.Diff;
using Amberkeep.Core.Exclusions;
using Amberkeep.Core.Execution;
using Amberkeep.Core.Paths;
using Amberkeep.Core.Safety;

return args switch
{
    ["diff", var source, var target, .. var rest] => await DiffAsync(source, target, rest, apply: false),
    ["mirror", var source, var target, .. var rest] => await DiffAsync(source, target, rest, apply: true),
    ["make-sandbox", var directory] => MakeSandbox(directory),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        Amberkeep.DevDiff – prints the mirror diff and optionally applies it.

        Usage:
          diff <source> <target> [diff options]                 read-only
          mirror <source> <target> [diff options] [--archive] [--verify] [--yes]
                                                                applies the diff to the target after confirmation
          make-sandbox <empty-or-missing-directory>

        Diff options:
          --content  --exclude <pattern>...  --no-default-exclusions
          --case auto|sensitive|insensitive  --no-mass-removal-guard
        """);
    return 1;
}

static async Task<int> DiffAsync(string source, string target, string[] rest, bool apply)
{
    var exclusions = new List<string>();
    var useDefaults = true;
    var options = DiffOptions.Default;
    var execution = new ExecutionOptions();
    var yes = false;

    for (var i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--archive" when apply:
                execution = execution with { ArchiveEnabled = true };
                break;
            case "--verify" when apply:
                execution = execution with { VerifyCopies = true };
                break;
            case "--yes" when apply:
                yes = true;
                break;
            case "--content":
                options = options with { CompareMode = CompareMode.Content };
                break;
            case "--exclude" when i + 1 < rest.Length:
                exclusions.Add(rest[++i]);
                break;
            case "--no-default-exclusions":
                useDefaults = false;
                break;
            case "--case" when i + 1 < rest.Length && Enum.TryParse<CaseSensitivity>(rest[i + 1], ignoreCase: true, out var caseSensitivity):
                options = options with { CaseSensitivity = caseSensitivity };
                i++;
                break;
            case "--no-mass-removal-guard":
                options = options with { MassRemovalGuard = new MassRemovalGuard(Enabled: false) };
                break;
            default:
                Console.Error.WriteLine($"Unknown option: {rest[i]}");
                return Usage();
        }
    }

    options = options with { Exclusions = [.. useDefaults ? DefaultExclusions.Patterns : [], .. exclusions] };

    var result = await new DiffEngine().AnalyzeAsync(new DiffRequest(source, target, options));

    Console.WriteLine($"Source:  {result.SourcePath}  ({result.SourceFileCount} files)");
    Console.WriteLine($"Target:  {result.TargetPath}  ({result.TargetFileCount} files)");
    Console.WriteLine($"Compare: {options.CompareMode}, case {(options.CaseSensitivity.IsCaseSensitive() ? "sensitive" : "insensitive")}");
    Console.WriteLine();

    if (result.SafetyIssues.Count > 0)
    {
        Console.WriteLine("Safety:");
        foreach (var issue in result.SafetyIssues)
        {
            Console.WriteLine($"  [{issue.Severity}] {issue.Message}");
        }

        Console.WriteLine();
    }

    Console.WriteLine("Changes:");
    if (result.Items.Count == 0)
    {
        Console.WriteLine("  (none)");
    }

    foreach (var item in result.Items)
    {
        var check = item.DefaultSelected ? "[x]" : "[ ]";
        var size = item.Kind == Amberkeep.Core.FileSystem.EntryKind.File
            ? FormatBytes((item.Source ?? item.Target)!.Length)
            : "<dir>";
        var extra = item.Category switch
        {
            DiffCategory.CaseRename => $"  (target: {item.Target!.RelativePath})",
            DiffCategory.Moved => $"  (moved from {item.Target!.RelativePath})",
            DiffCategory.Error => $"  ({item.Message})",
            _ when item.Source is not null && item.Target is not null
                && item.Source.RelativePath != item.Target.RelativePath => $"  (renamed from {item.Target.RelativePath})",
            _ => string.Empty,
        };
        Console.WriteLine($"  {check} {item.Category,-13} {size,10}  {item.RelativePath}{extra}");
    }

    PrintIssues("Errors", result.Errors);
    PrintIssues("Skipped", result.Skipped);

    var s = result.Summary;
    Console.WriteLine();
    Console.WriteLine("Summary:");
    foreach (var (category, totals) in s.ByCategory.OrderBy(p => p.Key))
    {
        Console.WriteLine($"  {category,-13} {totals.Files,6} files {totals.Directories,6} dirs {FormatBytes(totals.Bytes),10}");
    }

    Console.WriteLine($"  {"Unchanged",-13} {s.UnchangedFiles,6} files");
    Console.WriteLine($"  To copy: {FormatBytes(s.BytesToCopy)}, to replace: {FormatBytes(s.BytesToReplace)}, to remove: {FormatBytes(s.BytesToRemove)}, moved: {FormatBytes(s.BytesToMove)}");
    Console.WriteLine($"  Free space needed: {FormatBytes(s.RequiredFreeSpace(archiveEnabled: false))} (with archive: {FormatBytes(s.RequiredFreeSpace(archiveEnabled: true))})");
    Console.WriteLine();
    Console.WriteLine(result.IsBlocked ? "Result: BLOCKED" : result.RequiresApproval ? "Result: requires approval" : "Result: OK");

    if (!apply || result.IsBlocked)
    {
        return result.IsBlocked ? 2 : 0;
    }

    var selection = result.Items.Where(i => i.DefaultSelected).ToHashSet();
    if (selection.Count == 0)
    {
        Console.WriteLine("Nothing to do.");
        return 0;
    }

    // A safety check that asks for approval always needs an explicit answer, --yes is not enough.
    if (!yes || result.RequiresApproval)
    {
        Console.Write($"Apply {selection.Count} changes to {result.TargetPath}{(execution.ArchiveEnabled ? " (with archive)" : string.Empty)}? [y/N] ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Cancelled.");
            return 0;
        }
    }

    var outcome = await new MirrorExecutor().ExecuteAsync(result, selection, execution with { Approved = true });

    Console.WriteLine();
    Console.WriteLine($"Outcome: {outcome.Outcome} in {outcome.Duration.TotalSeconds:0.0} s");
    if (outcome.RefusedReason is not null)
    {
        Console.WriteLine($"  Refused: {outcome.RefusedReason}");
    }

    Console.WriteLine($"  Copied {outcome.CopiedFiles}, replaced {outcome.ReplacedFiles}, removed {outcome.RemovedFiles} files and {outcome.RemovedDirectories} folders, " +
        $"created {outcome.CreatedDirectories} folders, moved {outcome.MovedFiles}, renamed {outcome.RenamedEntries}, timestamps {outcome.UpdatedTimestamps}, archived {outcome.ArchivedFiles}, {FormatBytes(outcome.CopiedBytes)} written");
    if (outcome.ArchiveFolder is not null)
    {
        Console.WriteLine($"  Archive: {outcome.ArchiveFolder}");
    }

    foreach (var failure in outcome.Failures)
    {
        Console.WriteLine($"  FAILED {failure.Operation} {failure.RelativePath}: {failure.Message}");
    }

    foreach (var note in outcome.Notes)
    {
        Console.WriteLine($"  Note: {note}");
    }

    return outcome.Outcome is ExecutionOutcome.Completed ? 0 : 3;
}

static void PrintIssues(string title, IReadOnlyList<DiffIssue> issues)
{
    if (issues.Count == 0)
    {
        return;
    }

    Console.WriteLine();
    Console.WriteLine($"{title}:");
    foreach (var issue in issues)
    {
        Console.WriteLine($"  {issue.Side,-6} {issue.RelativePath}  ({issue.Message})");
    }
}

static string FormatBytes(long bytes)
{
    string[] units = ["B", "KB", "MB", "GB", "TB"];
    double value = bytes;
    var unit = 0;
    while (value >= 1024 && unit < units.Length - 1)
    {
        value /= 1024;
        unit++;
    }

    return unit == 0
        ? $"{bytes} B"
        : value.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unit];
}

static int MakeSandbox(string directory)
{
    if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
    {
        Console.Error.WriteLine($"Refusing to use a non-empty directory: {directory}");
        return 1;
    }

    var t0 = DateTime.UtcNow.Date.AddDays(-1).AddHours(12);
    var source = Path.GetFullPath(Path.Combine(directory, "source"));
    var target = Path.GetFullPath(Path.Combine(directory, "target"));

    void Write(string root, string relativePath, string content, DateTime time)
    {
        var full = Path.Join(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, time);
    }

    void Both(string relativePath, string sourceContent, DateTime sourceTime, string targetContent, DateTime targetTime)
    {
        Write(source, relativePath, sourceContent, sourceTime);
        Write(target, relativePath, targetContent, targetTime);
    }

    Both("unchanged.txt", "same", t0, "same", t0);
    Both("within-2s-tolerance.txt", "same", t0.AddSeconds(1), "same", t0);
    Both("modified.txt", "new longer content", t0.AddHours(1), "old", t0);
    Both("target-newer.txt", "source", t0, "edited on target", t0.AddHours(2));
    Both("timestamp-only.txt", "same content", t0.AddHours(3), "same content", t0);
    Both("same-size-and-time-different-content.txt", "aaaa", t0, "bbbb", t0);
    Write(source, "new.txt", "new", t0);
    Write(source, "new-folder/inner.txt", "inner", t0);
    Directory.CreateDirectory(Path.Combine(source, "empty-new-folder"));
    Write(target, "removed.txt", "gone", t0);
    Write(target, "removed-folder/a.txt", "a", t0);
    Write(target, "removed-folder/b.txt", "b", t0);
    Write(source, "Photos/Holiday.JPG", "img", t0);
    Write(target, "photos/holiday.jpg", "img", t0);
    Write(source, "report", "now a file", t0);
    Write(target, "report/old.txt", "was a folder", t0);
    Both("cache.tmp", "temp", t0.AddHours(1), "temp old", t0);
    Write(target, "node_modules/lib.js", "kept because excluded", t0);
    Write(target, ".amberkeep/target.json", "{}", t0);

    Console.WriteLine($"""
        Sandbox created in {Path.GetFullPath(directory)}

        Try (dotnet run --project tools/Amberkeep.DevDiff -- ...):
          diff "{source}" "{target}"
          diff "{source}" "{target}" --content
          diff "{source}" "{target}" --exclude *.tmp --exclude node_modules/
        """);
    return 0;
}
