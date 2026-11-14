using Amberkeep.App.Services;
using Amberkeep.Core.Diff;
using Amberkeep.Storage.Config;
using Amberkeep.Storage.Running;
using Amberkeep.Storage.Targets;

namespace Amberkeep.App.ViewModels;

/// <summary>
/// Runs a job against a target the way the dashboard does it: analyze, then either show the diff for approval
/// or run straight away, then show progress and the result.
/// </summary>
internal sealed class JobFlow(IShell shell)
{
    private AppServices Services => shell.Services;

    /// <param name="job">The job.</param>
    /// <param name="target">The target.</param>
    /// <param name="runWithoutReview">Run without showing the diff when nothing requires approval.</param>
    /// <param name="onFinished">Called when the user leaves the result or the diff; the dashboard by default.</param>
    /// <param name="onStarted">Called once the analysis really starts (no other backup was running).</param>
    public async Task StartAsync(JobConfig job, TargetConfig target, bool runWithoutReview, Action? onFinished = null, Action? onStarted = null)
    {
        var finish = onFinished ?? shell.ShowDashboard;

        // A card or notification may hold an older copy of the job: always use the saved one (e.g. a just enabled
        // "Review changes before every run"). A job or target removed in the meantime is not run.
        if (Services.Config.FindJob(job.Id) is not { } currentJob || currentJob.FindTarget(target.Id) is not { } currentTarget)
        {
            finish();
            return;
        }

        job = currentJob;
        target = currentTarget;
        var title = $"{job.Name} → {target.Name}";
        if (!Services.Gate.TryEnter())
        {
            await ShowBusyAsync(shell);
            finish();
            return;
        }

        onStarted?.Invoke();
        var busy = new BusyViewModel("Analyzing", title);
        shell.Show(busy);

        PreparedRun? analyzed = null;
        string? failure = null;
        try
        {
            Services.Activity.Analyzing(title);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(busy.Token, Services.Gate.Token);
            // Off the UI thread: finding the target and checking paths can wait for an unreachable network drive.
            var token = cancellation.Token;
            analyzed = await Task.Run(() => Services.Runner.AnalyzeAsync(job, target, busy.Progress, token));
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user, or the app is quitting.
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the user gets a message and the window does not stay on "Analyzing".
            if (ex is not (IOException or UnauthorizedAccessException or InvalidOperationException))
            {
                ErrorLog.Write(ex, $"Analysis of {title}");
            }

            failure = ex.Message;
        }
        finally
        {
            // Released before finish(): Run all starts the next target from there.
            Services.Activity.Idle();
            Services.Gate.Exit();
        }

        if (failure is not null)
        {
            await shell.Dialogs.ShowMessageAsync("Analysis failed", failure);
        }

        if (analyzed is not { } prepared)
        {
            finish();
            return;
        }

        if (prepared.TargetMoved && !Services.IsReadOnly)
        {
            try
            {
                Services.Update(config => config.FindJob(job.Id) is { } current
                    ? config.WithJob(current.WithTarget(prepared.Target))
                    : config);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Only the remembered location is lost; the target is found again next time.
            }
        }

        if (prepared.Diff is not { } diff)
        {
            await shell.Dialogs.ShowMessageAsync(
                prepared.Status.State == TargetState.Unverified ? "Target not verified" : "Target not connected",
                prepared.Status.Reason ?? $"'{target.Name}' was not found. Connect the disk and try again.");
            finish();
            return;
        }

        var needsReview = !runWithoutReview || job.RequireApproval || diff.IsBlocked || diff.RequiresApproval;
        if (!needsReview)
        {
            if (diff.Items.Count == 0)
            {
                await shell.Dialogs.ShowMessageAsync("Up to date", $"{title} has no changes.");
                finish();
                return;
            }

            await RunAsync(prepared, diff.Items.Where(i => i.DefaultSelected).ToHashSet(), approved: false, finish);
            return;
        }

        shell.Show(new DiffViewModel(
            shell,
            diff,
            title,
            job.Archive.Enabled,
            selection => RunAsync(prepared, selection, approved: true, finish),
            finish));
    }

    /// <summary>Tells the user that a backup already runs (usually an automatic one).</summary>
    internal static Task ShowBusyAsync(IShell shell) => shell.Dialogs.ShowMessageAsync(
        "A backup is running",
        "Another backup is running, possibly in the background. Try again when it has finished.");

    /// <summary>
    /// Runs <paramref name="execute"/> as the only backup and mirrors its progress to the tray.
    /// Returns null when another backup is running.
    /// </summary>
    internal static async Task<RunOutcome?> GuardedAsync(
        AppServices services,
        string title,
        IProgress<Core.Execution.ExecutionProgress> progress,
        Func<IProgress<Core.Execution.ExecutionProgress>, CancellationToken, Task<RunOutcome>> execute)
    {
        if (!services.Gate.TryEnter())
        {
            return null;
        }

        try
        {
            services.Activity.Running(title, 0);
            var tracked = new ForwardingProgress(progress, p =>
                services.Activity.Running(title, p.TotalBytes > 0 ? 100.0 * p.CopiedBytes / p.TotalBytes : 0));
            return await execute(tracked, services.Gate.Token);
        }
        finally
        {
            services.Activity.Idle();
            services.Gate.Exit();
        }
    }

    private Task RunAsync(PreparedRun prepared, IReadOnlySet<DiffItem> selection, bool approved, Action finish)
    {
        var title = $"{prepared.Job.Name} → {prepared.Target.Name}";
        var run = new RunViewModel(
            shell,
            title,
            prepared.Job.SourcePath,
            prepared.Status.Path!,
            async (progress, ct) => await GuardedAsync(
                    Services,
                    title,
                    progress,
                    async (tracked, gateToken) =>
                    {
                        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, gateToken);
                        return RunOutcome.From(await Services.Runner.ExecuteAsync(prepared, selection, approved, tracked, cancellation.Token));
                    })
                ?? throw new InvalidOperationException("Another backup is running, possibly in the background. Try again when it has finished."),
            finish);
        shell.Show(run);
        return run.StartAsync();
    }

    private sealed class ForwardingProgress(IProgress<Core.Execution.ExecutionProgress> inner, Action<Core.Execution.ExecutionProgress> also)
        : IProgress<Core.Execution.ExecutionProgress>
    {
        public void Report(Core.Execution.ExecutionProgress value)
        {
            inner.Report(value);
            also(value);
        }
    }
}
