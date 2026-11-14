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
    /// <param name="path">The folder to check, normally a target.</param>
    /// <param name="ignoreTargetId">A target that may be at this path (the one being moved).</param>
    /// <param name="pendingJob">The job being edited, in place of its saved version.</param>
    /// <param name="programDirectory">Amberkeep's own folder, which no target may overlap.</param>
    /// <param name="onDisk">Also compare where the folders really are (junctions, links, drive aliases); touches the disk.</param>
    /// <param name="ownerJobId">The job the ignored target belongs to; a target with the same id in another job is
    /// still checked (an imported or hand-copied job may carry another job's target id). Defaults to the pending job.</param>
    public string? FindPathConflict(
        string path,
        Guid? ignoreTargetId = null,
        JobConfig? pendingJob = null,
        string? programDirectory = null,
        bool onDisk = false,
        Guid? ownerJobId = null)
    {
        var owner = ownerJobId ?? pendingJob?.Id;
        if (programDirectory is not null && SafetyChecks.CheckOverlap(programDirectory, path, onDisk) is not null)
        {
            return $"The folder overlaps the folder of Amberkeep itself ({programDirectory}); a mirror would remove its settings and history.";
        }

        var jobs = pendingJob is null ? Jobs : Jobs.Where(j => j.Id != pendingJob.Id).Append(pendingJob);
        foreach (var job in jobs)
        {
            if (Path.IsPathFullyQualified(job.SourcePath) && SafetyChecks.CheckOverlap(job.SourcePath, path, onDisk) is not null)
            {
                return $"The folder overlaps the source of the backup '{job.Name}' ({job.SourcePath}).";
            }

            foreach (var target in job.Targets.Where(t => (t.Id != ignoreTargetId || (owner is not null && job.Id != owner)) && Path.IsPathFullyQualified(t.LastKnownPath)))
            {
                if (SafetyChecks.CheckOverlap(target.LastKnownPath, path, onDisk) is not null)
                {
                    return $"The folder overlaps the target '{target.Name}' of the backup '{job.Name}' ({target.LastKnownPath}).";
                }
            }
        }

        return null;
    }

    /// <summary>Validates <paramref name="job"/> on its own and against all other jobs.</summary>
    /// <param name="job">The job to check.</param>
    /// <param name="programDirectory">Amberkeep's own folder: no target may overlap it, and the source may not be inside it.</param>
    /// <param name="onDisk">Also compare where the folders really are (junctions, links, drive aliases); touches the disk,
    /// so it is used when saving, not on every keystroke.</param>
    public IReadOnlyList<string> Validate(JobConfig job, string? programDirectory = null, bool onDisk = false)
    {
        ArgumentNullException.ThrowIfNull(job);
        var errors = job.Validate(onDisk).ToList();
        if (errors.Count > 0)
        {
            return errors;
        }

        if (programDirectory is not null
            && PathOverlap.Check(job.SourcePath, programDirectory, CaseSensitivity.Auto.IsCaseSensitive(), onDisk)
                is PathRelation.Same or PathRelation.SourceInsideTarget)
        {
            errors.Add($"The source is the folder of Amberkeep itself or inside it ({programDirectory}).");
        }

        var others = this with { Jobs = Jobs.Where(j => j.Id != job.Id).ToList() };
        foreach (var target in job.Targets)
        {
            // Other jobs, plus the job's own other targets.
            var context = others.WithJob(job with { SourcePath = string.Empty });
            if (context.FindPathConflict(target.LastKnownPath, target.Id, programDirectory: programDirectory, onDisk: onDisk, ownerJobId: job.Id) is { } conflict)
            {
                errors.Add($"Target '{target.Name}': {conflict}");
            }
        }

        foreach (var other in others.Jobs)
        {
            foreach (var target in other.Targets.Where(t => Path.IsPathFullyQualified(t.LastKnownPath)))
            {
                if (SafetyChecks.CheckOverlap(job.SourcePath, target.LastKnownPath, onDisk) is not null)
                {
                    errors.Add($"The source overlaps the target '{target.Name}' of the backup '{other.Name}'.");
                }
            }
        }

        return errors;
    }

    /// <summary>Longest archive retention that can be stored (about 100 years).</summary>
    public const int MaxRetentionDays = 36_500;

    /// <summary>
    /// Makes a hand-edited file safe to use: nulls become defaults, values out of range are brought into range
    /// (never in the direction of deleting more), unknown option values become defaults, and duplicate ids get new
    /// ones (such a target then has to be located again; it is never written to by mistake).
    /// </summary>
    /// <param name="fixes">Receives a short description of every value that had to be changed.</param>
    internal AppConfig Normalized(List<string>? fixes = null)
    {
        var jobIds = new HashSet<Guid>();
        var targetIds = new HashSet<Guid>();
        var jobs = new List<JobConfig>();
        foreach (var j in (Jobs ?? []).Where(j => j is not null))
        {
            var name = j.Name ?? string.Empty;
            void Fix(string what) => fixes?.Add($"'{name}': {what}");

            var id = j.Id;
            if (!jobIds.Add(id))
            {
                id = Guid.NewGuid();
                jobIds.Add(id);
                Fix("the backup had the same id as another one and got a new one.");
            }

            var archive = j.Archive ?? new ArchiveSettings();
            if (archive.RetentionDays is < 1)
            {
                // Zero or less would delete every archived version after each run; keeping them is the safe side.
                archive = archive with { RetentionDays = null };
                Fix("an archive retention below one day was changed to 'keep forever'.");
            }
            else if (archive.RetentionDays > MaxRetentionDays)
            {
                archive = archive with { RetentionDays = MaxRetentionDays };
                Fix($"the archive retention was limited to {MaxRetentionDays:N0} days.");
            }

            var guard = j.MassRemovalGuard ?? new MassRemovalGuard();
            if (guard.Percent is < 0 or > 100 || guard.Count < 0)
            {
                // A negative count becomes the default, not 0: 0 would switch that limit off.
                guard = guard with { Percent = Math.Clamp(guard.Percent, 0, 100), Count = guard.Count < 0 ? new MassRemovalGuard().Count : guard.Count };
                Fix("the limits of 'Ask before removing many files' were out of range and were corrected.");
            }

            var compareMode = Enum.IsDefined(j.CompareMode) ? j.CompareMode : CompareMode.SizeAndTime;
            var caseSensitivity = Enum.IsDefined(j.CaseSensitivity) ? j.CaseSensitivity : CaseSensitivity.Auto;
            if (compareMode != j.CompareMode || caseSensitivity != j.CaseSensitivity)
            {
                Fix("an unknown comparison option was reset to the default.");
            }

            var trigger = j.Trigger ?? new TriggerConfig();
            var schedule = (trigger.Schedule ?? new ScheduleConfig()).Normalized();
            if (!Enum.IsDefined(schedule.Kind))
            {
                schedule = schedule with { Kind = ScheduleKind.None };
                Fix("an unknown schedule was switched off.");
            }

            if (schedule.IntervalHours is < 1 or > 744)
            {
                schedule = schedule with { IntervalHours = Math.Clamp(schedule.IntervalHours, 1, 744) };
                Fix("the schedule interval was limited to 1–744 hours.");
            }

            if (schedule.DayOfMonth is < 1 or > 31)
            {
                schedule = schedule with { DayOfMonth = Math.Clamp(schedule.DayOfMonth, 1, 31) };
                Fix("the day of the month of the schedule was limited to 1–31.");
            }

            if (schedule.Kind == ScheduleKind.Weekly && schedule.DaysOfWeek.Count == 0)
            {
                schedule = schedule with { Kind = ScheduleKind.None };
                Fix("a weekly schedule without days was switched off.");
            }

            var targets = new List<TargetConfig>();
            foreach (var t in (j.Targets ?? []).Where(t => t is not null))
            {
                var target = t with
                {
                    Name = t.Name ?? string.Empty,
                    RelativePath = t.RelativePath ?? string.Empty,
                    LastKnownPath = t.LastKnownPath ?? string.Empty,
                };
                if (!targetIds.Add(target.Id))
                {
                    target = target with { Id = Guid.NewGuid() };
                    targetIds.Add(target.Id);
                    Fix($"the target '{target.Name}' had the same id as another target and has to be located again.");
                }

                targets.Add(target);
            }

            jobs.Add(j with
            {
                Id = id,
                Name = name,
                SourcePath = j.SourcePath ?? string.Empty,
                Exclusions = (j.Exclusions ?? []).Where(e => e is not null).ToList(),
                CompareMode = compareMode,
                CaseSensitivity = caseSensitivity,
                MassRemovalGuard = guard,
                Archive = archive,
                Trigger = trigger with { Schedule = schedule },
                Targets = targets,
            });
        }

        return this with { Settings = Settings ?? new AppSettings(), Jobs = jobs };
    }
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

public enum ScheduleKind
{
    None,
    Daily,
    Weekly,
    Monthly,
    Interval,
}

/// <summary>A recurring time for automatic runs, in local time (see <see cref="ScheduleCalculator"/>).</summary>
public sealed record ScheduleConfig
{
    public ScheduleKind Kind { get; init; } = ScheduleKind.None;

    /// <summary>Time of day for daily, weekly and monthly schedules.</summary>
    public TimeOnly Time { get; init; } = new(20, 0);

    /// <summary>Days for weekly schedules; at least one.</summary>
    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; } = [DayOfWeek.Monday];

    /// <summary>Day of the month (1–31) for monthly schedules. A month without that day runs on its last day.</summary>
    public int DayOfMonth { get; init; } = 1;

    /// <summary>Hours between runs for interval schedules.</summary>
    public int IntervalHours { get; init; } = 6;

    /// <summary>
    /// When the schedule was set (by the editor). Times before it are never "missed", so switching a schedule on
    /// does not start a catch-up run at once. Null in files written before this existed. Not part of equality:
    /// it describes when, not what.
    /// </summary>
    public DateTimeOffset? SinceUtc { get; init; }

    /// <summary>The single weekly day of files written before multi-day schedules; moved into <see cref="DaysOfWeek"/> on load.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("dayOfWeek")]
    public DayOfWeek? LegacyDayOfWeek { get; init; }

    public bool Equals(ScheduleConfig? other) =>
        other is not null
        && Kind == other.Kind
        && Time == other.Time
        && DaysOfWeek.SequenceEqual(other.DaysOfWeek)
        && DayOfMonth == other.DayOfMonth
        && IntervalHours == other.IntervalHours
        && LegacyDayOfWeek == other.LegacyDayOfWeek;

    public override int GetHashCode() => HashCode.Combine(Kind, Time, DaysOfWeek.Count, DayOfMonth, IntervalHours);

    /// <summary>Weekly days in calendar order (Monday first), without duplicates; an old single day is taken over.</summary>
    internal ScheduleConfig Normalized() => this with
    {
        DaysOfWeek = (LegacyDayOfWeek is { } legacy ? [legacy] : DaysOfWeek ?? [])
            .Where(Enum.IsDefined)
            .Distinct()
            .OrderBy(MondayFirst)
            .ToList(),
        LegacyDayOfWeek = null,
    };

    /// <summary>Monday = 0 … Sunday = 6.</summary>
    internal static int MondayFirst(DayOfWeek day) => ((int)day + 6) % 7;
}

/// <summary>When a job runs by itself (SPEC 4.6). By default it never does.</summary>
public sealed record TriggerConfig
{
    /// <summary>Run when the target disk is connected.</summary>
    public bool OnTargetConnected { get; init; }

    public ScheduleConfig Schedule { get; init; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAutomatic => OnTargetConnected || Schedule.Kind != ScheduleKind.None;
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
    /// <param name="onDisk">Also compare where the source and targets really are (junctions, links, drive aliases).</param>
    public IReadOnlyList<string> Validate(bool onDisk = false)
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

        if (Archive.RetentionDays is < 1 or > AppConfig.MaxRetentionDays)
        {
            errors.Add($"Archive retention must be between 1 and {AppConfig.MaxRetentionDays:N0} days.");
        }

        if (Trigger.Schedule is { Kind: ScheduleKind.Interval, IntervalHours: < 1 or > 24 * 31 })
        {
            errors.Add("The schedule interval must be between 1 hour and 31 days.");
        }

        if (Trigger.Schedule.Kind == ScheduleKind.Weekly && Trigger.Schedule.DaysOfWeek.Count == 0)
        {
            errors.Add("Choose at least one day for the weekly schedule.");
        }

        if (Trigger.Schedule is { Kind: ScheduleKind.Monthly, DayOfMonth: < 1 or > 31 })
        {
            errors.Add("The day of the month must be between 1 and 31.");
        }

        foreach (var target in Targets)
        {
            if (!Path.IsPathFullyQualified(target.LastKnownPath))
            {
                errors.Add($"Target '{target.Name}' must be a full path.");
            }
            else if (Path.IsPathFullyQualified(SourcePath)
                     && SafetyChecks.CheckOverlap(SourcePath, target.LastKnownPath, onDisk) is { } overlap)
            {
                errors.Add($"Target '{target.Name}': {overlap.Message}");
            }
        }

        return errors;
    }
}
