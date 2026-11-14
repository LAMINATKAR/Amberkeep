using System.Text.Json;
using Amberkeep.Core.FileSystem;

namespace Amberkeep.Storage.Config;

/// <summary>A file with exported backup jobs (SPEC 5).</summary>
public sealed record ExportFile
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;

    public string App { get; init; } = "Amberkeep";

    public IReadOnlyList<JobConfig> Jobs { get; init; } = [];
}

public sealed record SkippedImport(string Name, string Reason);

public sealed record ImportResult(
    AppConfig Config,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Replaced,
    IReadOnlyList<SkippedImport> Skipped);

/// <summary>
/// Moves backup jobs between computers. Job and target ids are kept, so the markers on the disks
/// are recognized on the new computer as well. Settings and history are not exported.
/// </summary>
public static class ConfigTransfer
{
    public static void Export(IWritableFileSystem fileSystem, AppConfig config, IEnumerable<Guid> jobIds, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        var ids = jobIds.ToHashSet();
        var file = new ExportFile { Jobs = config.Jobs.Where(j => ids.Contains(j.Id)).ToList() };
        StorageFiles.Write(fileSystem, path, file, StorageJsonContext.Default.ExportFile);
    }

    /// <exception cref="InvalidDataException">The file is not a readable Amberkeep export.</exception>
    public static IReadOnlyList<JobConfig> ReadImport(IFileSystem fileSystem, string path)
    {
        ExportFile? file;
        try
        {
            file = StorageFiles.Read(fileSystem, path, StorageJsonContext.Default.ExportFile);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The file is not an Amberkeep export ({ex.Message}).", ex);
        }

        if (file is null || !string.Equals(file.App, "Amberkeep", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The file is not an Amberkeep export.");
        }

        if (file.Format > ExportFile.CurrentFormat)
        {
            throw new InvalidDataException("The file was exported by a newer version of Amberkeep. Update Amberkeep first.");
        }

        return new AppConfig { Jobs = file.Jobs ?? [] }.Normalized().Jobs;
    }

    /// <summary>Adds imported jobs to <paramref name="config"/>. Invalid jobs are skipped with a reason.</summary>
    /// <param name="config">Current configuration.</param>
    /// <param name="imported">Jobs read by <see cref="ReadImport"/>.</param>
    /// <param name="replaceExisting">Replace jobs with the same id; otherwise they are skipped.</param>
    public static ImportResult Merge(AppConfig config, IReadOnlyList<JobConfig> imported, bool replaceExisting, string? programDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(imported);

        var current = config;
        var added = new List<string>();
        var replaced = new List<string>();
        var skipped = new List<SkippedImport>();

        foreach (var original in imported)
        {
            // A target id that belongs to another job (a job copied by hand in the file) gets a new id: two jobs
            // must never recognize the same disk folder as theirs. Such a target then has to be located.
            var foreign = current.Jobs.Where(j => j.Id != original.Id).SelectMany(j => j.Targets).Select(t => t.Id).ToHashSet();
            var job = original with
            {
                Targets = original.Targets.Select(t => foreign.Contains(t.Id) ? t with { Id = Guid.NewGuid() } : t).ToList(),
            };
            var exists = current.FindJob(job.Id) is not null;
            if (exists && !replaceExisting)
            {
                skipped.Add(new SkippedImport(job.Name, "A backup with the same identity already exists."));
                continue;
            }

            var errors = current.Validate(job, programDirectory, onDisk: true);
            if (errors.Count > 0)
            {
                skipped.Add(new SkippedImport(job.Name, errors[0]));
                continue;
            }

            current = current.WithJob(job);
            (exists ? replaced : added).Add(job.Name);
        }

        return new ImportResult(current, added, replaced, skipped);
    }
}
