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
public sealed class ActivityMonitor
{
    private readonly Lock _lock = new();
    private ActivityState _state = new(ActivityKind.Idle, "Amberkeep", 0, false);
    private bool _reviewsWaiting;
    private bool _problem;

    private bool Attention => _reviewsWaiting || _problem;

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

    public void Analyzing(string title) => Set(new ActivityState(ActivityKind.Analyzing, $"Analyzing {title}", 0, Attention));

    public void Running(string title, double percent) =>
        Set(new ActivityState(ActivityKind.Running, $"{title} – {percent:0} %", percent, Attention));

    public void Idle() => Set(new ActivityState(ActivityKind.Idle, IdleText, 0, Attention));

    /// <summary>Automatic runs wait for the user's review.</summary>
    public void SetReviewsWaiting(bool value)
    {
        _reviewsWaiting = value;
        Refresh();
    }

    /// <summary>A background run had problems; shown until the user opens the history or a later run succeeds.</summary>
    public void ReportProblem()
    {
        _problem = true;
        Refresh();
    }

    public void ClearProblem()
    {
        _problem = false;
        Refresh();
    }

    private string IdleText => Attention ? "Amberkeep – needs your attention" : "Amberkeep";

    private void Refresh()
    {
        var current = State;
        Set(current with
        {
            NeedsAttention = Attention,
            Text = current.Kind == ActivityKind.Idle ? IdleText : current.Text,
        });
    }

    private void Set(ActivityState state)
    {
        lock (_lock)
        {
            _state = state;
        }

        Changed?.Invoke(this, state);
    }
}

/// <summary>Only one analysis or backup runs at a time, whether started by the user or automatically.</summary>
public sealed class RunGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool IsBusy => _semaphore.CurrentCount == 0;

    public bool TryEnter() => _semaphore.Wait(0);

    public void Exit() => _semaphore.Release();
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
    private CancellationTokenSource? _current;

    /// <summary>Stops a running automatic backup (e.g. when the app quits).</summary>
    public void Cancel() => _current?.Cancel();

    public async Task<BackgroundRunResult> RunAsync(JobConfig job, TargetConfig target, CancellationToken cancellationToken = default)
    {
        if (!services.Gate.TryEnter())
        {
            return BackgroundRunResult.Busy;
        }

        var title = $"{job.Name} → {target.Name}";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _current = cancellation;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"{title}: failed", ex.Message, null);
            services.Activity.ReportProblem();
            return BackgroundRunResult.CompletedWithProblems;
        }
        finally
        {
            _current = null;
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

    private void TrySaveLocation(JobConfig job, TargetConfig target)
    {
        try
        {
            if (!services.IsReadOnly)
            {
                services.Update(config => config.FindJob(job.Id) is { } current ? config.WithJob(current.WithTarget(target)) : config);
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
        _timer ??= services.Time.CreateTimer(_ => _ = TickAsync(), null, TimeSpan.Zero, Interval);
    }

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
                var connected = services.Locator.Locate(target).IsConnected;
                var known = _connected.TryGetValue(target.Id, out var wasConnected);
                _connected[target.Id] = connected;

                if (!_primed || !connected || config.Settings.AutomaticRunsPaused || services.PendingReviews.Contains(target.Id))
                {
                    continue;
                }

                // A target seen for the first time (new job, trigger just switched on) only sets the baseline.
                var isDue = job.Trigger.OnTargetConnected && known && !wasConnected;
                if (!isDue && job.Trigger.Schedule.Kind != ScheduleKind.None)
                {
                    var lastRun = History(now).FirstOrDefault(r => r.JobId == job.Id && r.TargetId == target.Id)?.StartedUtc;
                    if (_lastAttempt.TryGetValue(target.Id, out var attempt) && (lastRun is null || attempt > lastRun))
                    {
                        lastRun = attempt;
                    }

                    isDue = ScheduleCalculator.IsDue(job.Trigger.Schedule, lastRun, target.CreatedUtc, now, _zone);
                }

                if (isDue)
                {
                    due.Add((job, target));
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
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    public void Dispose()
    {
        // The token source is not disposed: a tick that is still finishing may read its token.
        _disposed = true;
        _timer?.Dispose();
        _stop.Cancel();
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
