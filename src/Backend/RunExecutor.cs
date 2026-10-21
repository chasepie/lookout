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
    /// <summary>The Watch was deleted while this Run executed, so there was nothing to record it against.</summary>
    Deleted
}

public sealed record RunExecution(RunExecutionStatus Status, Run? Run = null);

/// <summary>
/// Executes a Watch's current Script and records the Run, for both scheduled and on-demand Runs.
/// Callers admit the Run first through <see cref="RunningWatches"/> and <see cref="RunSlots"/>.
/// </summary>
public sealed class RunExecutor(LookoutDbContext database, IRunnerClient runner, TimeProvider timeProvider)
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

        var startedAt = timeProvider.GetUtcNow();
        var runRequest = new RunnerRequest(
            Source: script.Source,
            FileKind: script.FileKind,
            Context: new ScriptContext(watch.NotificationUrl),
            TimeoutMs: watch.TimeoutSeconds * 1000);
        var result = await runner.ExecuteAsync(runRequest, cancellationToken);

        var evaluation = RunEvaluation.Evaluate(watch.Baseline, watch.FailureStreak, result.Outcome, result.Content,
            result.ErrorMessage);
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
            if (!await database.Watches.AnyAsync(item => item.Id == watchId, CancellationToken.None))
            {
                return new RunExecution(RunExecutionStatus.Deleted);
            }
            if (exception is DbUpdateConcurrencyException)
            {
                return new RunExecution(RunExecutionStatus.Superseded);
            }
            throw;
        }

        return new RunExecution(RunExecutionStatus.Recorded, run);
    }
}
