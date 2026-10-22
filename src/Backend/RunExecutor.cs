using ChasePie.Lookout.Backend.Controllers;
using ChasePie.Lookout.Backend.Events;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend;

public enum RunExecutionStatus
{
    Recorded,
    WatchNotFound,
    NoScriptVersion,
    /// <summary>Another Run of the Watch was recorded while this one executed, so this one was discarded.</summary>
    Superseded,
    /// <summary>A new Script version was uploaded while this Run executed, so this Run of the old one was discarded.</summary>
    ScriptReplaced,
    /// <summary>The Watch was deleted while this Run executed, so there was nothing to record it against.</summary>
    Deleted
}

public sealed record RunExecution(RunExecutionStatus Status, Run? Run = null);

/// <summary>
/// Executes a Watch's current Script, records the Run and sends its notifications, for both scheduled and on-demand Runs.
/// Callers admit the Run first through <see cref="RunningWatches"/> and <see cref="RunSlots"/>.
/// Announces the Run's start and finish, and any change of the Watch's status, through <see cref="WatchEvents"/>.
/// </summary>
public sealed class RunExecutor(
    LookoutDbContext database,
    IRunnerClient runner,
    NotificationDispatcher notifications,
    WatchEvents events,
    TimeProvider timeProvider)
{
    public async Task<RunExecution> ExecuteAsync(Guid watchId, CancellationToken cancellationToken)
    {
        var watch = await database.Watches
            .Include(watch => watch.CurrentScriptVersion)
            .SingleOrDefaultAsync(watch => watch.Id == watchId, cancellationToken);
        if (watch is null)
        {
            return new RunExecution(RunExecutionStatus.WatchNotFound);
        }

        var script = watch.CurrentScriptVersion;
        if (script is null)
        {
            return new RunExecution(RunExecutionStatus.NoScriptVersion);
        }

        var status = WatchSummary.StatusOf(watch);
        var startedAt = timeProvider.GetUtcNow();
        await events.RunStartedAsync(watchId, startedAt);
        RunExecution execution;
        IReadOnlyList<Notification> toSend;
        try
        {
            (execution, toSend) = await RunScriptAsync(watch, script, startedAt, cancellationToken);
        }
        catch
        {
            // A Run that started always finishes, so the app stops showing it as Running.
            await events.RunFinishedAsync(watchId, null);
            throw;
        }

        await events.RunFinishedAsync(watchId, execution.Run);
        // Read back, as the tracked Watch misses the user pausing or resuming it while the Run executed. A Run doesn't
        // change a paused Watch's status, and pausing announced it.
        if (execution.Run is not null
            && await WatchSummary.StatusAsync(database.Watches, watchId, CancellationToken.None) is { } newStatus
            && newStatus != status && newStatus != WatchStatus.Paused)
        {
            await events.StatusChangedAsync(watchId, newStatus);
        }

        // After the events, so the app isn't held up by sending. Like the save, notifications of a finished Run still go
        // out if shutdown started; senders time out by themselves.
        await notifications.NotifyAsync(watchId, toSend, CancellationToken.None);
        return execution;
    }

    /// <summary>Runs the Script and records the Run, returning the notifications it calls for once recorded.</summary>
    private async Task<(RunExecution Execution, IReadOnlyList<Notification> Notifications)> RunScriptAsync(Watch watch,
        ScriptVersion script, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var watchId = watch.Id;
        var runRequest = new RunnerRequest(
            Source: script.Source,
            FileKind: script.FileKind,
            Context: new ScriptContext(watch.NotificationUrl),
            TimeoutMs: watch.TimeoutSeconds * 1000);
        var result = await runner.ExecuteAsync(runRequest, cancellationToken);

        var evaluation = RunEvaluation.Evaluate(watch, result.Outcome, result.Content, result.Details, result.ErrorMessage);
        var run = new Run
        {
            WatchId = watchId,
            ScriptVersionId = script.Id,
            Outcome = evaluation.Outcome,
            Content = result.Content,
            Details = result.Details,
            ErrorMessage = evaluation.ErrorMessage,
            DurationMs = result.DurationMs,
            StartedAt = startedAt,
            FinishedAt = timeProvider.GetUtcNow(),
            IsChange = evaluation.IsChange
        };
        watch.Baseline = evaluation.Baseline;
        watch.FailureStreak = evaluation.FailureStreak;
        watch.FailureStreakNotified = evaluation.FailureStreakNotified;
        watch.Revision++;
        database.Runs.Add(run);

        try
        {
            // The Run has finished, so record it even if shutdown started while the runner was replying.
            await database.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException exception)
        {
            // A deleted Watch fails either the Run's foreign key or the Watch's concurrency check, whichever EF sends first.
            var current = await database.Watches.AsNoTracking()
                .Where(item => item.Id == watchId)
                .Select(item => new { item.CurrentScriptVersionId })
                .SingleOrDefaultAsync(CancellationToken.None);
            if (current is null)
            {
                return (new RunExecution(RunExecutionStatus.Deleted), []);
            }
            if (exception is DbUpdateConcurrencyException)
            {
                var re = new RunExecution(current.CurrentScriptVersionId == script.Id
                    ? RunExecutionStatus.Superseded
                    : RunExecutionStatus.ScriptReplaced);
                return (re, []);
            }
            throw;
        }

        return (new RunExecution(RunExecutionStatus.Recorded, run), evaluation.Notifications);
    }
}
