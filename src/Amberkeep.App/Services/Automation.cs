using Amberkeep.App.ViewModels;
using Amberkeep.Core.Diff;
using Amberkeep.Core.Execution;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.History;

namespace Amberkeep.App.Services;

public enum ActivityKind
{
    Idle,
    Analyzing,
    Running,
}

public sealed record ActivityState(ActivityKind Kind, string Text, double Percent, bool NeedsAttention);

/// <summary>What Amberkeep is doing right now – shown by the tray icon and its tooltip. Thread-safe.</summary>
/// <remarks>
/// Every change is computed and stored under one lock, so concurrent updates from a background run and the UI
/// cannot write back an older state. Listeners should read <see cref="State"/> when they handle
/// <see cref="Changed"/>, because notifications from different threads may arrive out of order.
/// </remarks>
public sealed class ActivityMonitor
{
    private readonly Lock _lock = new();
    private ActivityState _state = new(ActivityKind.Idle, "Amberkeep", 0, false);
    private bool _reviewsWaiting;
    private bool _problem;

    public event EventHandler<ActivityState>? Changed;

    public ActivityState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public void Analyzing(string title) => Update(_ => new ActivityState(ActivityKind.Analyzing, $"Analyzing {title}", 0, false));

    public void Running(string title, double percent) =>
        Update(_ => new ActivityState(ActivityKind.Running, $"{title} – {percent:0} %", percent, false));

    public void Idle() => Update(_ => new ActivityState(ActivityKind.Idle, string.Empty, 0, false));

    /// <summary>Automatic runs wait for the user's review.</summary>
    public void SetReviewsWaiting(bool value) => Update(current =>
    {
        _reviewsWaiting = value;
        return current;
    });

    /// <summary>A background run had problems; shown until the user opens the history or a later run succeeds.</summary>
    public void ReportProblem() => Update(current =>
    {
        _problem = true;
        return current;
    });

    public void ClearProblem() => Update(current =>
    {
        _problem = false;
        return current;
    });

    // The change, the attention flag and the idle text are applied together under the lock.
    private void Update(Func<ActivityState, ActivityState> change)
    {
        ActivityState state;
        lock (_lock)
        {
            state = change(_state);
            var attention = _reviewsWaiting || _problem;
            state = state with
            {
                NeedsAttention = attention,
                Text = state.Kind == ActivityKind.Idle ? (attention ? "Amberkeep – needs your attention" : "Amberkeep") : state.Text,
            };
            _state = state;
        }

        Changed?.Invoke(this, state);
    }
}

/// <summary>
/// Only one analysis or backup runs at a time, whether started by the user or automatically. The work holding the
/// gate observes <see cref="Token"/>, so quitting can stop it safely and wait for it (<see cref="StopAsync"/>).
/// </summary>
public sealed class RunGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Lock _lock = new();
    private CancellationTokenSource _stop = new();
    private TaskCompletionSource? _released;
    private bool _stopping;

    public bool IsBusy => _semaphore.CurrentCount == 0;

    /// <summary>Cancelled when the app stops while the gate is held; link it into the work's cancellation.</summary>
    public CancellationToken Token
    {
        get
        {
            lock (_lock)
            {
                return _stop.Token;
            }
        }
    }

    public bool TryEnter()
    {
        lock (_lock)
        {
            if (_stopping)
            {
                return false;
            }
        }

        if (!_semaphore.Wait(0))
        {
            return false;
        }

        lock (_lock)
        {
            // Stopped between the first check and taking the semaphore: give it back.
            if (_stopping)
            {
                _semaphore.Release();
                return false;
            }

            // Not disposed: work that has just let go may still hold the old token.
            _stop = new CancellationTokenSource();
            _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return true;
    }

    public void Exit()
    {
        TaskCompletionSource? released;
        lock (_lock)
        {
            released = _released;
            _released = null;
        }

        _semaphore.Release();
        released?.TrySetResult();
    }

    /// <summary>Lets go of the gate and lets no new work start (the holder is about to close the app).</summary>
    public void ExitAndStop()
    {
        lock (_lock)
        {
            _stopping = true;
        }

        Exit();
    }

    /// <summary>
    /// No new work may start; the work holding the gate is cancelled, and this waits until it has let go
    /// (at most <paramref name="timeout"/>), so it can clean up its temporary files and write its history record.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        Task? released;
        lock (_lock)
        {
            _stopping = true;
            released = _released?.Task;
            _stop.Cancel();
        }

        if (released is not null)
        {
            await Task.WhenAny(released, Task.Delay(timeout)).ConfigureAwait(false);
        }
    }
}

/// <summary>An automatic analysis found changes that need the user's approval.</summary>
public sealed record PendingReview(JobConfig Job, TargetConfig Target, int Changes, DateTimeOffset FoundUtc)
{
    public string Title => $"{Job.Name} → {Target.Name}";
}

/// <summary>Automatic analyses waiting for approval (kept in memory). Thread-safe.</summary>
public sealed class PendingReviews
{
    private readonly Lock _lock = new();
    private readonly List<PendingReview> _items = [];

    public event EventHandler? Changed;

    public IReadOnlyList<PendingReview> Items
    {
        get
        {
            lock (_lock)
            {
                return _items.ToList();
            }
        }
    }

    public bool Contains(Guid targetId)
    {
        lock (_lock)
        {
            return _items.Any(i => i.Target.Id == targetId);
        }
    }

    public void Add(PendingReview review)
    {
        lock (_lock)
        {
            _items.RemoveAll(i => i.Target.Id == review.Target.Id);
            _items.Add(review);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(Guid targetId)
    {
        lock (_lock)
        {
            if (_items.RemoveAll(i => i.Target.Id == targetId) == 0)
            {
                return;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Shows a short message to the user (a toast in the app; a fake in tests).</summary>
public interface INotifier
{
    /// <param name="title">First line.</param>
    /// <param name="message">Details.</param>
    /// <param name="onClick">Called when the user clicks the notification.</param>
    void Show(string title, string message, Action? onClick = null);
}

public enum BackgroundRunResult
{
    /// <summary>Another run is in progress; try again later.</summary>
    Busy,
    NotConnected,
    UpToDate,
    WaitingForReview,
    Completed,
    CompletedWithProblems,
}

/// <summary>
/// One automatic run (SPEC 4.6): analyze; nothing to do → done; approval needed → pending review and a
/// notification; otherwise run with the default selection and notify about the result.
/// </summary>
public sealed class BackgroundRunner(AppServices services, INotifier notifier, Action<PendingReview>? openReview = null)
{
    public async Task<BackgroundRunResult> RunAsync(JobConfig job, TargetConfig target, CancellationToken cancellationToken = default)
    {
        if (!services.Gate.TryEnter())
        {
            return BackgroundRunResult.Busy;
        }

        // The job may have been edited or deleted since the check that started this run.
        if (services.Config.FindJob(job.Id) is not { } currentJob || currentJob.FindTarget(target.Id) is not { } currentTarget)
        {
            services.Gate.Exit();
            return BackgroundRunResult.NotConnected;
        }

        job = currentJob;
        target = currentTarget;
        var title = $"{job.Name} → {target.Name}";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, services.Gate.Token);
        try
        {
            services.Activity.Analyzing(title);
            var prepared = await services.Runner.AnalyzeAsync(job, target, cancellationToken: cancellation.Token).ConfigureAwait(false);
            if (prepared.TargetMoved)
            {
                TrySaveLocation(job, prepared.Target);
            }

            if (prepared.Diff is not { } diff)
            {
                return BackgroundRunResult.NotConnected;
            }

            if (diff.Items.Count == 0)
            {
                return BackgroundRunResult.UpToDate;
            }

            if (job.RequireApproval || diff.IsBlocked || diff.RequiresApproval)
            {
                var review = new PendingReview(job, prepared.Target, diff.Items.Count, services.Time.GetUtcNow());
                services.PendingReviews.Add(review);
                Notify(
                    $"{title}: {Format.Count(diff.Items.Count, "change", "changes")}",
                    diff.IsBlocked ? "The backup is blocked – open it to see why." : "Review the changes to run the backup.",
                    () => openReview?.Invoke(review));
                return BackgroundRunResult.WaitingForReview;
            }

            var progress = new SynchronousProgress<ExecutionProgress>(p =>
                services.Activity.Running(title, p.TotalBytes > 0 ? 100.0 * p.CopiedBytes / p.TotalBytes : 0));
            var record = await services.Runner.ExecuteAsync(
                prepared,
                diff.Items.Where(i => i.DefaultSelected).ToHashSet(),
                approved: false,
                progress,
                cancellation.Token).ConfigureAwait(false);

            var ok = record.Outcome == ExecutionOutcome.Completed;
            Notify(
                $"{title}: {Format.Outcome(record.Outcome).ToLowerInvariant()}",
                RunStats.Summary(RunOutcome.From(record)),
                null);
            if (ok)
            {
                services.Activity.ClearProblem();
            }
            else
            {
                services.Activity.ReportProblem();
            }

            return ok ? BackgroundRunResult.Completed : BackgroundRunResult.CompletedWithProblems;
        }
        catch (OperationCanceledException)
        {
            return BackgroundRunResult.Busy;
        }
        catch (Exception ex)
        {
            // Nothing may escape: this runs on a timer, and an unhandled exception would be lost or end the app.
            if (ex is not (IOException or UnauthorizedAccessException or InvalidOperationException))
            {
                ErrorLog.Write(ex, $"Automatic run {title}");
            }

            Notify($"{title}: failed", ex.Message, null);
            services.Activity.ReportProblem();
            return BackgroundRunResult.CompletedWithProblems;
        }
        finally
        {
            services.Activity.Idle();
            services.Gate.Exit();
        }
    }

    private void Notify(string title, string message, Action? onClick)
    {
        if (services.Config.Settings.Notifications)
        {
            notifier.Show(title, message, onClick);
        }
    }

    // Only the location of a target that still exists is updated: one removed in the meantime is not added back.
    private void TrySaveLocation(JobConfig job, TargetConfig target)
    {
        try
        {
            if (!services.IsReadOnly)
            {
                services.Update(config => config.FindJob(job.Id) is { } current && current.FindTarget(target.Id) is not null
                    ? config.WithJob(current.WithTarget(target))
                    : config);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Only the remembered location is lost.
        }
    }

    // Progress<T> would post to a synchronization context; background runs report directly.
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

/// <summary>
/// Starts automatic runs (SPEC 4.6): when a target is connected, or when its schedule is due.
/// Checks every 15 seconds; one run at a time.
/// </summary>
public sealed class AutomationEngine(
    AppServices services,
    Func<JobConfig, TargetConfig, CancellationToken, Task<BackgroundRunResult>> run,
    TimeZoneInfo? zone = null) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly TimeZoneInfo _zone = zone ?? TimeZoneInfo.Local;
    private readonly Dictionary<Guid, bool> _connected = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastAttempt = [];
    private static readonly TimeSpan HistoryRefresh = TimeSpan.FromMinutes(10);

    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;
    private int _ticking;
    private bool _primed;
    private volatile bool _disposed;
    private IReadOnlyList<RunRecord>? _history;
    private DateTimeOffset _historyLoaded;

    public void Start()
    {
        if (_timer is null)
        {
            // A new record (also of a manual run) moves the next scheduled time at once, not after the cache expires.
            services.History.Changed += OnHistoryChanged;
            _timer = services.Time.CreateTimer(_ => _ = TickAsync(), null, TimeSpan.Zero, Interval);
        }
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => _history = null;

    /// <summary>One check of all automatic targets. The first call only records which targets are connected.</summary>
    public async Task TickAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var config = services.Config;
            var automatic = config.Jobs
                .Where(j => j.Trigger.IsAutomatic)
                .SelectMany(j => j.Targets.Select(t => (Job: j, Target: t)))
                .ToList();
            var now = services.Time.GetUtcNow();
            var due = new List<(JobConfig Job, TargetConfig Target)>();

            foreach (var (job, target) in automatic)
            {
                // One broken job (e.g. a hand-edited value) must not stop the checks of all others.
                try
                {
                    if (IsDue(config, job, target, now))
                    {
                        due.Add((job, target));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ErrorLog.Write(ex, $"Automatic check of {job.Name} → {target.Name}");
                    services.Activity.ReportProblem();
                }
            }


            _primed = true;
            foreach (var (job, target) in due)
            {
                if (_disposed)
                {
                    return;
                }

                var result = await run(job, target, _stop.Token).ConfigureAwait(false);
                if (result == BackgroundRunResult.Busy)
                {
                    // Try again on the next check (also for "connected" triggers).
                    _connected[target.Id] = false;
                    continue;
                }

                _lastAttempt[target.Id] = services.Time.GetUtcNow();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A check runs on a timer: an error must not disappear unnoticed, and the next check tries again.
            ErrorLog.Write(ex, "Automatic check");
            services.Activity.ReportProblem();
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    public void Dispose()
    {
        // The token source is not disposed: a tick that is still finishing may read its token.
        _disposed = true;
        services.History.Changed -= OnHistoryChanged;
        _timer?.Dispose();
        _stop.Cancel();
    }

    private bool IsDue(AppConfig config, JobConfig job, TargetConfig target, DateTimeOffset now)
    {
        var connected = services.Locator.Locate(target).IsConnected;
        var known = _connected.TryGetValue(target.Id, out var wasConnected);
        _connected[target.Id] = connected;

        if (!_primed || !connected || config.Settings.AutomaticRunsPaused || services.PendingReviews.Contains(target.Id))
        {
            return false;
        }

        // A target seen for the first time (new job, trigger just switched on) only sets the baseline.
        if (job.Trigger.OnTargetConnected && known && !wasConnected)
        {
            return true;
        }

        if (job.Trigger.Schedule.Kind == ScheduleKind.None)
        {
            return false;
        }

        // Records "from the future" (a wrong clock) are ignored, or they would count as the last run forever.
        var lastRun = History(now).FirstOrDefault(r => r.JobId == job.Id && r.TargetId == target.Id && r.StartedUtc <= now)?.StartedUtc;
        if (_lastAttempt.TryGetValue(target.Id, out var attempt) && (lastRun is null || attempt > lastRun))
        {
            lastRun = attempt;
        }

        return ScheduleCalculator.IsDue(job.Trigger.Schedule, lastRun, target.CreatedUtc, now, _zone);
    }

    /// <summary>Last runs from the history, re-read every 10 minutes instead of on every check.</summary>
    private IReadOnlyList<RunRecord> History(DateTimeOffset now)
    {
        if (_history is null || now - _historyLoaded > HistoryRefresh || now < _historyLoaded)
        {
            _history = services.History.List();
            _historyLoaded = now;
        }

        return _history;
    }
}
