using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Scheduling;

/// <summary>
/// Starts Runs of Watches as they fall due, reading what is due from the database.
/// It never catches up: a Watch that fell due while Lookout was down, or several times while it waited for
/// a free <see cref="Runs">Run slot</see>, runs at most once and its next Run is scheduled from now.
/// A due Watch that is already Running skips this Run. Startup also redoes daily and cron Watches' next Runs if the
/// install's time zone has changed. It looks for due Watches every <see cref="PollInterval"/>, unless it doesn't poll,
/// which leaves starting due Runs to whoever calls <see cref="StartDueRunsAsync"/>, as tests do.
/// </summary>
public sealed partial class Scheduler(
    bool polls,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    WatchSchedules schedules,
    Runs runs,
    ILogger<Scheduler> logger)
    : BackgroundService
{
    /// <summary>How often the scheduler looks for due Watches.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Finish before the host starts serving, so a Watch created after startup is never mistaken for a missed one.
        await RescheduleStaleRuns(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!polls)
        {
            LogNotPolling(logger);
            return;
        }
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        try
        {
            // StartAsync has just rescheduled every Watch that was due, so nothing is due before the first tick.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await StartDueRunsAsync(stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    LogDispatchFailed(logger, error);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Lookout is stopping; Runs waits for the Runs in progress.
        }
    }

    /// <summary>
    /// Starts a Run of each due Watch, in the order they fell due, returning each one's admission. A Run waiting for a
    /// Run slot is claimed once it has one, which records it as starting then and schedules the next from then; one
    /// whose Watch was paused, or rescheduled, meanwhile is dropped. A due Watch already Running skips this Run, unless
    /// that's its own scheduled Run waiting for a slot, from an earlier call.
    /// </summary>
    public async Task<IReadOnlyList<Admission>> StartDueRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var now = timeProvider.GetUtcNow();
        var due = await database.Watches.AsNoTracking()
            .Where(watch => watch.NextRunAt <= now && !watch.IsPaused)
            .OrderBy(watch => watch.NextRunAt)
            .ToListAsync(cancellationToken);

        // Every due Watch is admitted or refused before any skip is claimed, as Runs admits without waiting.
        var admissions = due.ConvertAll(watch => runs.Run(watch.Id, (runDatabase, token) => TryClaimWatch(runDatabase, watch, token)));
        foreach (var (watch, admission) in due.Zip(admissions))
        {
            if (admission is Admitted admitted)
            {
                _ = LogEndingAsync(watch.Id, admitted.Finished);
            }
            else if (admission is not Refused { Reason: RunRefusal.WaitingForSlot, Gated: true })
            {
                // A Watch that is already running skips this Run rather than overlapping it.
                await TryClaimWatch(database, watch, cancellationToken);
            }
        }
        return admissions;
    }

    /// <summary>Logs a scheduled Run that failed, or wasn't recorded for a reason other than not being claimed.</summary>
    private async Task LogEndingAsync(Guid watchId, Task<RunExecution> finished)
    {
        try
        {
            var execution = await finished;
            if (execution.Status is not (RunExecutionStatus.Recorded or RunExecutionStatus.NotClaimed))
            {
                LogRunNotRecorded(logger, watchId, execution.Status);
            }
        }
        catch (OperationCanceledException) when (finished.IsCanceled)
        {
            // Lookout stopped before the Run finished, so there is nothing to record.
        }
        catch (Exception error)
        {
            LogRunFailed(logger, error, watchId);
        }
    }

    private async Task RescheduleStaleRuns(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var now = timeProvider.GetUtcNow();
        var timeZone = schedules.TimeZone.Id;

        // Daily and cron times are wall-clock times, so they move when the install's time zone changes.
        // A paused Watch has no next Run until it is resumed.
        var stale = await database.Watches
            .Where(watch => !watch.IsPaused)
            .Where(watch =>
                watch.NextRunAt == null
                || watch.NextRunAt <= now
                || (watch.Schedule.Kind != ScheduleKind.Interval && watch.NextRunTimeZone != timeZone))
            .ToListAsync(cancellationToken);
        foreach (var watch in stale)
        {
            watch.NextRunAt = schedules.NextRunAt(watch.Schedule, watch.JitterMinutes, watch.LastScheduledRunAt);
            watch.NextRunTimeZone = timeZone;
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records the Watch's due Run as starting now and schedules the next one from now, unless the Watch was paused or
    /// rescheduled since it was read, or another backend instance has already claimed this Run.
    /// </summary>
    private async Task<bool> TryClaimWatch(LookoutDbContext database, Watch watch, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var claimed = await database.Watches
            .Where(item => item.Id == watch.Id && item.NextRunAt == watch.NextRunAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.NextRunAt, schedules.NextRunAt(watch.Schedule, watch.JitterMinutes, now))
                .SetProperty(item => item.NextRunTimeZone, schedules.TimeZone.Id)
                .SetProperty(item => item.LastScheduledRunAt, now), cancellationToken);
        return claimed == 1;
    }

    [LoggerMessage(LogLevel.Error, "Could not start due Runs.")]
    private static partial void LogDispatchFailed(ILogger logger, Exception error);

    [LoggerMessage(LogLevel.Warning, "Scheduled Run of Watch {WatchId} was not recorded: {Status}.")]
    private static partial void LogRunNotRecorded(ILogger logger, Guid watchId, RunExecutionStatus status);

    [LoggerMessage(LogLevel.Error, "Scheduled Run of Watch {WatchId} failed.")]
    private static partial void LogRunFailed(ILogger logger, Exception error, Guid watchId);

    [LoggerMessage(LogLevel.Warning, "Scheduling:Poll is off, so no Watch runs on its schedule.")]
    private static partial void LogNotPolling(ILogger logger);
}
