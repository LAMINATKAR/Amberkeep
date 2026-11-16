using System.Diagnostics;
using System.Text.Json.Nodes;
using Amberkeep.App.ViewModels;
using Amberkeep.Core.Diff;
using Amberkeep.Core.Exclusions;
using Amberkeep.Core.Execution;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Paths;
using Amberkeep.Core.Safety;
using Amberkeep.Core.Scanning;
using Amberkeep.Storage.Archive;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.History;
using Amberkeep.Tests.App;
using Amberkeep.Tests.Infrastructure;
using Amberkeep.Tests.Storage;

namespace Amberkeep.Tests.Safety;

/// <summary>Fixes from the security audit (1.1.0): import of backups from a file.</summary>
public sealed class ImportAuditTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Imported_backups_keep_their_automatic_runs_and_approval_setting()
    {
        var (photos, _) = _fx.JobWithTarget("Photos");
        var automatic = photos with
        {
            RequireApproval = false,
            Trigger = new TriggerConfig
            {
                OnTargetConnected = true,
                Schedule = new ScheduleConfig { Kind = ScheduleKind.Daily, Time = new TimeOnly(21, 30) },
            },
        };

        var result = ConfigTransfer.Merge(new AppConfig(), [automatic], replaceExisting: false);

        var job = Assert.Single(result.Config.Jobs);
        Assert.False(job.RequireApproval);
        Assert.True(job.Trigger.OnTargetConnected);
        Assert.Equal(ScheduleKind.Daily, job.Trigger.Schedule.Kind);
    }

    [Fact]
    public void A_path_with_an_invalid_character_is_skipped_with_a_reason()
    {
        var broken = new JobConfig { Name = "Broken", SourcePath = Path.Combine(_fx.BasePath, "a\0b") };

        var result = ConfigTransfer.Merge(new AppConfig(), [broken], replaceExisting: false);

        Assert.Contains("valid path", Assert.Single(result.Skipped).Reason, StringComparison.Ordinal);
        Assert.Empty(result.Config.Jobs);
    }

    [Theory]
    [InlineData(@"\\server\share\Backup", true)]
    [InlineData("//server/share", true)]
    [InlineData(@"\\?\UNC\server\share", true)]
    [InlineData(@"\\?\C:\Backup", false)]
    [InlineData(@"\\.\C:\Backup", false)]
    [InlineData(@"C:\Backup", false)]
    [InlineData("/media/usb/Backup", false)]
    [InlineData("", false)]
    public void Network_paths_are_recognized_from_the_text_alone(string path, bool network) =>
        Assert.Equal(network, ConfigTransfer.IsNetworkPath(path));

    [Fact]
    public void Summary_shows_paths_runs_approval_and_network_paths_before_importing()
    {
        var job = new JobConfig
        {
            Name = "Documents",
            SourcePath = @"C:\Users\someone\Documents",
            RequireApproval = false,
            MassRemovalGuard = new MassRemovalGuard(Enabled: false),
            Trigger = new TriggerConfig { OnTargetConnected = true },
            Targets = [new TargetConfig { Name = "Share", LastKnownPath = @"\\server\drop" }],
        };

        var summary = SettingsViewModel.ImportSummary([job]);

        Assert.Contains(@"From: C:\Users\someone\Documents", summary, StringComparison.Ordinal);
        Assert.Contains(@"To: Share – \\server\drop (network)", summary, StringComparison.Ordinal);
        Assert.Contains("Runs: On connect", summary, StringComparison.Ordinal);
        Assert.Contains("Asks before every run: no", summary, StringComparison.Ordinal);
        Assert.Contains("Guard against removing many files: off", summary, StringComparison.Ordinal);
        Assert.Contains("another computer", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_calls_the_guard_off_when_both_of_its_limits_are_off()
    {
        var job = new JobConfig { Name = "Photos", SourcePath = @"D:\Photos", MassRemovalGuard = new MassRemovalGuard(Enabled: true, Percent: 100, Count: 0) };

        Assert.Contains("Guard against removing many files: off", SettingsViewModel.ImportSummary([job]), StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_of_a_manual_backup_says_so()
    {
        var job = new JobConfig { Name = "Photos", SourcePath = @"D:\Photos" };

        var summary = SettingsViewModel.ImportSummary([job]);

        Assert.Contains("Runs: only when you start it", summary, StringComparison.Ordinal);
        Assert.Contains("Asks before every run: yes", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Guard", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("another computer", summary, StringComparison.Ordinal);
    }
}

/// <summary>Fixes from the security audit (1.1.0): the import dialog.</summary>
public sealed class ImportDialogAuditTests : IDisposable
{
    private readonly AppFixture _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Cancelling_the_summary_imports_nothing()
    {
        var (job, _) = _app.SavedJob();
        var file = Path.Combine(_app.BasePath, "backups.json");
        ConfigTransfer.Export(PhysicalFileSystem.Instance, _app.Services.Config, [job.Id], file);
        _app.Services.Save(new AppConfig());
        _app.Dialogs.OpenFiles.Enqueue(file);

        await new SettingsViewModel(_app.Shell).ImportCommand.ExecuteAsync(null);

        Assert.Empty(_app.Services.Config.Jobs);
        Assert.Contains(_app.Dialogs.Messages, m => m == "Import 1 backup?");
    }

    [Fact]
    public async Task A_linked_archive_shows_a_message_instead_of_an_error()
    {
        var (job, target) = _app.SavedJob();
        var elsewhere = Directory.CreateDirectory(Path.Combine(_app.BasePath, "elsewhere")).FullName;
        var archive = ArchiveCleaner.ArchiveRoot(target.LastKnownPath);
        Links.CreateDirectoryLinkOrSkip(archive, elsewhere);
        try
        {
            var vm = new ArchiveViewModel(_app.Shell, job, target, target.LastKnownPath);

            await vm.LoadAsync();

            Assert.True(vm.IsEmpty);
            Assert.Contains(_app.Dialogs.Messages, m => m.StartsWith("The archive cannot be opened", StringComparison.Ordinal) && m.Contains("link or junction", StringComparison.Ordinal));
        }
        finally
        {
            Links.RemoveDirectoryLink(archive);
        }
    }
}

/// <summary>Fixes from the security audit (1.1.0): archive, restore and marker never go through links.</summary>
public sealed class ArchiveLinkAuditTests : IDisposable
{
    private const string OldSnapshot = "2020-01-01_120000";
    private readonly StorageFixture _fx = new();
    private readonly ArchiveBrowser _browser = new(PhysicalFileSystem.Instance);

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void A_linked_archive_is_not_listed_cleaned_up_or_deleted()
    {
        var (_, target) = _fx.JobWithTarget();
        var elsewhere = _fx.Create("elsewhere");
        var foreign = Directory.CreateDirectory(Path.Combine(elsewhere, OldSnapshot)).FullName;
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "keep");
        var archive = ArchiveCleaner.ArchiveRoot(target.LastKnownPath);
        Links.CreateDirectoryLinkOrSkip(archive, elsewhere);
        try
        {
            Assert.Throws<IOException>(() => _browser.ListSnapshots(target.LastKnownPath));
            Assert.Throws<IOException>(() => _browser.PurgeOld(target.LastKnownPath, TimeSpan.FromDays(1), DateTimeOffset.UtcNow));
            Assert.Throws<IOException>(() => _browser.DeleteSnapshot(
                new SnapshotInfo(OldSnapshot, Path.Join(archive, OldSnapshot), DateTimeOffset.UtcNow.AddYears(-5), 1, 4)));

            Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
        }
        finally
        {
            Links.RemoveDirectoryLink(archive);
        }
    }

    [Fact]
    public async Task Restore_does_not_write_through_a_link_in_the_destination()
    {
        var (snapshot, _) = Snapshot("a/x.txt");
        var destination = _fx.Create("restore-here");
        var elsewhere = _fx.Create("elsewhere");
        Links.CreateDirectoryLinkOrSkip(Path.Combine(destination, "a"), elsewhere);
        try
        {
            var result = await _browser.RestoreAsync(snapshot, ["a/x.txt"], destination, overwrite: true, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("link or junction", Assert.Single(result.Failures).Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        }
        finally
        {
            Links.RemoveDirectoryLink(Path.Combine(destination, "a"));
        }
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("a/../../escaped.txt")]
    [InlineData("a//x.txt")]
    [InlineData("a/x.")]
    [InlineData("NUL")]
    public async Task Restore_refuses_names_that_leave_the_destination(string relative)
    {
        var (snapshot, _) = Snapshot("a/x.txt");
        var destination = _fx.Create("restore-here");

        var result = await _browser.RestoreAsync(snapshot, [relative], destination, overwrite: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Restored);
        Assert.Single(result.Failures);
        Assert.False(File.Exists(Path.Combine(_fx.BasePath, "escaped.txt")));
    }

    [Fact]
    public async Task Restore_into_a_plain_folder_still_works()
    {
        var (snapshot, _) = Snapshot("a/x.txt");
        var destination = _fx.Create("restore-here");

        var result = await _browser.RestoreAsync(snapshot, ["a/x.txt"], destination, overwrite: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Restored);
        Assert.Equal("archived", File.ReadAllText(Path.Combine(destination, "a", "x.txt")));
    }

    [Fact]
    public void The_marker_is_not_written_through_a_linked_program_folder()
    {
        var folder = _fx.Create("volA/Backup/New");
        var elsewhere = _fx.Create("elsewhere");
        Links.CreateDirectoryLinkOrSkip(Path.Combine(folder, ".amberkeep"), elsewhere);
        try
        {
            var job = _fx.NewJob();
            var error = Assert.Throws<IOException>(() => _fx.Registry.AddTarget(_fx.Config, job, folder, "New", replaceForeignMarker: false));

            Assert.Contains("link or junction", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        }
        finally
        {
            Links.RemoveDirectoryLink(Path.Combine(folder, ".amberkeep"));
        }
    }

    private (SnapshotInfo Snapshot, string TargetPath) Snapshot(string file)
    {
        var (_, target) = _fx.JobWithTarget();
        var folder = Path.Join(ArchiveCleaner.ArchiveRoot(target.LastKnownPath), OldSnapshot);
        var path = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "archived");
        return (Assert.Single(_browser.ListSnapshots(target.LastKnownPath)), target.LastKnownPath);
    }
}

/// <summary>Fixes from the security audit (1.1.0): odd names, refused writes and slow patterns.</summary>
public sealed class RobustnessAuditTests : IDisposable
{
    private readonly MirrorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    // Only names every Windows version treats the same; others (e.g. CON, aux.c) depend on the version and are
    // decided by Windows itself.
    [Theory]
    [InlineData("file.", true)]
    [InlineData("file ", true)]
    [InlineData("...", true)]
    [InlineData("NUL", true)]
    [InlineData("file.txt", false)]
    [InlineData("CONFIG.SYS", false)]
    [InlineData(".amberkeep", false)]
    public void Names_that_Windows_changes_are_recognized(string name, bool changed)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows name rules.");
        Assert.Equal(changed, PathKey.IsChangedByWindows(_fx.Target.Root, name));
    }

    [Fact]
    public void The_name_check_follows_what_Windows_does_with_the_path()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows name rules.");
        foreach (var name in new[] { "CON", "con.txt", "aux.c", "COM1", "lpt1.log", "PRN" })
        {
            var expected = Path.Join(_fx.Target.Root, name);
            Assert.Equal(Path.GetFullPath(expected) != expected, PathKey.IsChangedByWindows(_fx.Target.Root, name));
        }
    }

    [Fact]
    public void Names_are_never_changed_on_other_systems()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Other systems only.");
        Assert.False(PathKey.IsChangedByWindows(_fx.Target.Root, "file."));
    }

    [Fact]
    public async Task A_source_file_named_like_a_device_is_backed_up_when_Windows_allows_the_name()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows name rules.");
        var expected = Path.Join(_fx.Source.Root, "aux.c");
        Assert.SkipWhen(Path.GetFullPath(expected) != expected, "This Windows version treats aux.c as a device.");
        _fx.Source.File("aux.c", "int main;");

        MirrorFixture.AssertCompleted(await _fx.AnalyzeAndMirrorAsync());

        Assert.Equal("int main;", File.ReadAllText(_fx.TargetPath("aux.c")));
    }

    [Fact]
    public async Task A_target_name_that_Windows_changes_is_left_alone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows name rules.");
        _fx.Source.File("file", "backup");
        _fx.Target.File("file", "backup");
        var odd = @"\\?\" + _fx.TargetPath("file.");
        File.WriteAllText(odd, "written by another system");
        try
        {
            var diff = await _fx.AnalyzeAsync();
            var result = await _fx.MirrorAsync(diff);

            Assert.DoesNotContain(diff.Items, i => i.RelativePath.StartsWith("file", StringComparison.Ordinal));
            Assert.Contains(diff.Skipped, s => s.RelativePath == "file.");
            MirrorFixture.AssertCompleted(result);
            Assert.Equal("backup", File.ReadAllText(_fx.TargetPath("file")));
            Assert.True(File.Exists(odd));
        }
        finally
        {
            File.Delete(odd);
        }
    }

    [Fact]
    public async Task A_refused_write_fails_only_its_item_and_the_run_goes_on()
    {
        _fx.Source.File("a.txt");
        var outside = Path.Combine(_fx.BasePath, "outside.txt");
        File.WriteAllText(outside, "not ours");
        var info = new FileInfo(outside);
        var diff = await _fx.AnalyzeAsync();
        var forged = new DiffItem
        {
            RelativePath = "../outside.txt",
            Kind = EntryKind.File,
            Category = DiffCategory.Removed,
            Target = new ScannedEntry("../outside.txt", EntryKind.File, info.Length, info.LastWriteTimeUtc),
        };
        diff = new DiffResult
        {
            SourcePath = diff.SourcePath,
            TargetPath = diff.TargetPath,
            Options = diff.Options,
            Items = [.. diff.Items, forged],
            Summary = diff.Summary,
            SourceFileCount = diff.SourceFileCount,
            TargetFileCount = diff.TargetFileCount + 1,
        };

        var result = await _fx.MirrorAsync(diff, selection: diff.Items);

        Assert.Equal(ExecutionOutcome.CompletedWithErrors, result.Outcome);
        Assert.Contains("Refusing to write", Assert.Single(result.Failures).Message, StringComparison.Ordinal);
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(_fx.TargetPath("a.txt")));
    }

    [Fact]
    public void An_exclusion_pattern_cannot_stall_the_analysis()
    {
        var matcher = new ExclusionMatcher(["/**e**e**e**e**e**e**e**e**e**e**e**e**Q"], caseSensitive: true);
        var path = string.Join('/', Enumerable.Repeat(new string('e', 30), 8));

        var stopwatch = Stopwatch.StartNew();
        var excluded = matcher.IsExcluded(path, isDirectory: false);

        Assert.False(excluded);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
    }
}

/// <summary>Fixes from the security audit (1.1.0): damaged history files.</summary>
public sealed class HistoryAuditTests : IDisposable
{
    private readonly StorageFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void A_record_with_missing_lists_is_read_with_empty_lists()
    {
        var record = new RunRecord { RunId = Guid.NewGuid(), JobId = Guid.NewGuid(), JobName = "Photos", StartedUtc = DateTimeOffset.UtcNow };
        _fx.History.Add(record, 100);
        var file = Directory.EnumerateFiles(_fx.History.Directory, "*.json").Single(f => !f.EndsWith(".changes.json", StringComparison.Ordinal));
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["notes"] = null;
        json["failures"] = new JsonArray(null, new JsonObject { ["relativePath"] = null, ["operation"] = "Copy", ["message"] = null });
        json["diffCounts"] = null;
        json["jobName"] = null;
        File.WriteAllText(file, json.ToJsonString());

        var read = Assert.Single(_fx.History.List());

        Assert.Empty(read.Notes);
        Assert.Empty(read.DiffCounts);
        Assert.Equal(string.Empty, read.JobName);
        var failure = Assert.Single(read.Failures);
        Assert.Equal(string.Empty, failure.RelativePath);
        Assert.Equal(string.Empty, failure.Message);
    }

    [Fact]
    public void A_damaged_change_list_drops_entries_without_a_path()
    {
        var record = new RunRecord
        {
            RunId = Guid.NewGuid(), JobId = Guid.NewGuid(), StartedUtc = DateTimeOffset.UtcNow,
            SelectedChanges = [new RecordedChange("a.txt", EntryKind.File, DiffCategory.New)],
        };
        _fx.History.Add(record, 100);
        var file = Directory.EnumerateFiles(_fx.History.Directory, "*.changes.json").Single();
        File.WriteAllText(file, """[null, { "path": null, "kind": "File", "category": "New" }, { "path": "b.txt", "kind": "File", "category": "New" }]""");

        var changes = _fx.History.ReadChanges(Assert.Single(_fx.History.List()));

        Assert.Equal("b.txt", Assert.Single(changes).Path);
    }
}
