using Amberkeep.Core.Diff;
using Amberkeep.Core.Exclusions;
using Amberkeep.Core.Execution;
using Amberkeep.Core.Paths;
using Amberkeep.Core.Safety;

namespace Amberkeep.Storage.Config;

/// <summary>Content of <c>amberkeep.json</c> next to the executable.</summary>
public sealed record AppConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public AppSettings Settings { get; init; } = new();

    public IReadOnlyList<JobConfig> Jobs { get; init; } = [];

    public JobConfig? FindJob(Guid jobId) => Jobs.FirstOrDefault(j => j.Id == jobId);

    /// <summary>Adds the job or replaces the job with the same id.</summary>
    public AppConfig WithJob(JobConfig job)
    {
        var jobs = Jobs.ToList();
        var index = jobs.FindIndex(j => j.Id == job.Id);
        if (index >= 0)
        {
            jobs[index] = job;
        }
        else
        {
            jobs.Add(job);
        }

        return this with { Jobs = jobs };
    }

    public AppConfig WithoutJob(Guid jobId) => this with { Jobs = Jobs.Where(j => j.Id != jobId).ToList() };

    /// <summary>
    /// Finds a source or target of any job (other than <paramref name="ignoreTargetId"/>) that is the same as,
    /// inside, or around <paramref name="path"/>. Nested targets would remove each other's backups.
    /// </summary>
    public string? FindPathConflict(string path, Guid? ignoreTargetId = null, JobConfig? pendingJob = null)
    {
        var jobs = pendingJob is null ? Jobs : Jobs.Where(j => j.Id != pendingJob.Id).Append(pendingJob);
        foreach (var job in jobs)
        {
            if (Path.IsPathFullyQualified(job.SourcePath) && SafetyChecks.CheckOverlap(job.SourcePath, path) is not null)
            {
                return $"The folder overlaps the source of the backup '{job.Name}' ({job.SourcePath}).";
            }

            foreach (var target in job.Targets.Where(t => t.Id != ignoreTargetId && Path.IsPathFullyQualified(t.LastKnownPath)))
            {
                if (SafetyChecks.CheckOverlap(target.LastKnownPath, path) is not null)
                {
                    return $"The folder overlaps the target '{target.Name}' of the backup '{job.Name}' ({target.LastKnownPath}).";
                }
            }
        }

        return null;
    }

    /// <summary>Validates <paramref name="job"/> on its own and against all other jobs.</summary>
    public IReadOnlyList<string> Validate(JobConfig job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var errors = job.Validate().ToList();
        if (errors.Count > 0)
        {
            return errors;
        }

        var others = this with { Jobs = Jobs.Where(j => j.Id != job.Id).ToList() };
        foreach (var target in job.Targets)
        {
            // Other jobs, plus the job's own other targets.
            var context = others.WithJob(job with { SourcePath = string.Empty });
            if (context.FindPathConflict(target.LastKnownPath, target.Id) is { } conflict)
            {
                errors.Add($"Target '{target.Name}': {conflict}");
            }
        }

        foreach (var other in others.Jobs)
        {
            foreach (var target in other.Targets.Where(t => Path.IsPathFullyQualified(t.LastKnownPath)))
            {
                if (SafetyChecks.CheckOverlap(job.SourcePath, target.LastKnownPath) is not null)
                {
                    errors.Add($"The source overlaps the target '{target.Name}' of the backup '{other.Name}'.");
                }
            }
        }

        return errors;
    }

    /// <summary>Replaces nulls from a hand-edited file with defaults, so the rest of the app can rely on them.</summary>
    internal AppConfig Normalized() => this with
    {
        Settings = Settings ?? new AppSettings(),
        Jobs = (Jobs ?? []).Where(j => j is not null).Select(j => j with
        {
            Name = j.Name ?? string.Empty,
            SourcePath = j.SourcePath ?? string.Empty,
            Exclusions = (j.Exclusions ?? []).Where(e => e is not null).ToList(),
            MassRemovalGuard = j.MassRemovalGuard ?? new MassRemovalGuard(),
            Archive = j.Archive ?? new ArchiveSettings(),
            Trigger = j.Trigger ?? new TriggerConfig(),
            Targets = (j.Targets ?? []).Where(t => t is not null).Select(t => t with
            {
                Name = t.Name ?? string.Empty,
                RelativePath = t.RelativePath ?? string.Empty,
                LastKnownPath = t.LastKnownPath ?? string.Empty,
            }).ToList(),
        }).ToList(),
    };
}

public sealed record AppSettings
{
    /// <summary>Number of run records kept in <c>history/</c>.</summary>
    public int HistoryLimit { get; init; } = 100;

    public bool CloseToTray { get; init; } = true;

    public bool StartWithSystem { get; init; }

    public bool Notifications { get; init; } = true;

    public bool AutomaticRunsPaused { get; init; }
}

public sealed record ArchiveSettings
{
    public bool Enabled { get; init; }

    /// <summary>Days an archive snapshot is kept; null keeps snapshots forever.</summary>
    public int? RetentionDays { get; init; } = 30;
}

public enum TriggerKind
{
    Manual,
}

/// <summary>When a job runs by itself. Extended in phase 6 (target connected, schedule).</summary>
public sealed record TriggerConfig
{
    public TriggerKind Kind { get; init; } = TriggerKind.Manual;
}

public sealed record TargetConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    /// <summary>Path below the volume root, used to find the target when the drive letter or mount point changes.</summary>
    public string RelativePath { get; init; } = string.Empty;

    public string LastKnownPath { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; }
}

public sealed record JobConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public IReadOnlyList<string> Exclusions { get; init; } = [.. DefaultExclusions.Patterns];

    public CompareMode CompareMode { get; init; } = CompareMode.SizeAndTime;

    public CaseSensitivity CaseSensitivity { get; init; } = CaseSensitivity.Auto;

    public bool IgnoreOneHourShift { get; init; } = true;

    public MassRemovalGuard MassRemovalGuard { get; init; } = new();

    public ArchiveSettings Archive { get; init; } = new();

    /// <summary>The diff must be reviewed and approved before every run.</summary>
    public bool RequireApproval { get; init; } = true;

    public bool VerifyCopies { get; init; }

    public TriggerConfig Trigger { get; init; } = new();

    public IReadOnlyList<TargetConfig> Targets { get; init; } = [];

    public TargetConfig? FindTarget(Guid targetId) => Targets.FirstOrDefault(t => t.Id == targetId);

    /// <summary>Adds the target or replaces the target with the same id.</summary>
    public JobConfig WithTarget(TargetConfig target)
    {
        var targets = Targets.ToList();
        var index = targets.FindIndex(t => t.Id == target.Id);
        if (index >= 0)
        {
            targets[index] = target;
        }
        else
        {
            targets.Add(target);
        }

        return this with { Targets = targets };
    }

    public JobConfig WithoutTarget(Guid targetId) => this with { Targets = Targets.Where(t => t.Id != targetId).ToList() };

    public DiffOptions ToDiffOptions() => new()
    {
        CompareMode = CompareMode,
        Exclusions = Exclusions,
        CaseSensitivity = CaseSensitivity,
        IgnoreOneHourShift = IgnoreOneHourShift,
        MassRemovalGuard = MassRemovalGuard,
    };

    public ExecutionOptions ToExecutionOptions(bool approved) => new()
    {
        ArchiveEnabled = Archive.Enabled,
        ArchiveRetention = Archive.RetentionDays is { } days ? TimeSpan.FromDays(days) : null,
        Approved = approved,
        VerifyCopies = VerifyCopies,
    };

    /// <summary>Problems that prevent the job from being saved; empty when the job is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("The job needs a name.");
        }

        if (!Path.IsPathFullyQualified(SourcePath))
        {
            errors.Add("The source folder must be a full path.");
        }

        try
        {
            MassRemovalGuard.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            errors.Add($"Mass removal guard: {ex.Message}");
        }

        if (Archive.RetentionDays is < 1)
        {
            errors.Add("Archive retention must be at least one day.");
        }

        foreach (var target in Targets)
        {
            if (!Path.IsPathFullyQualified(target.LastKnownPath))
            {
                errors.Add($"Target '{target.Name}' must be a full path.");
            }
            else if (Path.IsPathFullyQualified(SourcePath)
                     && SafetyChecks.CheckOverlap(SourcePath, target.LastKnownPath) is { } overlap)
            {
                errors.Add($"Target '{target.Name}': {overlap.Message}");
            }
        }

        return errors;
    }
}
