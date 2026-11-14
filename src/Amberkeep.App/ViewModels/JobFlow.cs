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
    public async Task StartAsync(JobConfig job, TargetConfig target, bool runWithoutReview, Action? onFinished = null)
    {
        var finish = onFinished ?? shell.ShowDashboard;
        var title = $"{job.Name} → {target.Name}";
        var busy = new BusyViewModel("Analyzing", title);
        shell.Show(busy);

        PreparedRun prepared;
        try
        {
            prepared = await Services.Runner.AnalyzeAsync(job, target, busy.Progress, busy.Token);
        }
        catch (OperationCanceledException)
        {
            finish();
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await shell.Dialogs.ShowMessageAsync("Analysis failed", ex.Message);
            finish();
            return;
        }

        if (prepared.TargetMoved && !Services.IsReadOnly)
        {
            var current = Services.Config.FindJob(job.Id);
            try
            {
                if (current is not null)
                {
                    Services.Save(Services.Config.WithJob(current.WithTarget(prepared.Target)));
                }
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

    private Task RunAsync(PreparedRun prepared, IReadOnlySet<DiffItem> selection, bool approved, Action finish)
    {
        var run = new RunViewModel(
            shell,
            $"{prepared.Job.Name} → {prepared.Target.Name}",
            prepared.Job.SourcePath,
            prepared.Status.Path!,
            async (progress, ct) => RunOutcome.From(await Services.Runner.ExecuteAsync(prepared, selection, approved, progress, ct)),
            finish);
        shell.Show(run);
        return run.StartAsync();
    }
}
