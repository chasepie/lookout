using System.Collections.Concurrent;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Scheduling;

/// <summary>
/// Starts Runs of Watches as they fall due, reading what is due from the database.
/// It never catches up: a Watch that fell due while Lookout was down, or several times while it waited for
/// a free <see cref="RunSlots">slot</see>, runs at most once and its next Run is scheduled from now.
/// Startup also redoes daily and cron Watches' next Runs if the install's time zone has changed.
/// </summary>
public sealed partial class Scheduler(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    WatchSchedules schedules,
    RunningWatches runningWatches,
    RunSlots runSlots,
    ILogger<Scheduler> logger)
    : BackgroundService
{
    /// <summary>How often the scheduler looks for due Watches.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Task, byte> _runs = new();

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Finish before the host starts serving, so a Watch created after startup is never mistaken for a missed one.
        await RescheduleStaleRuns(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        try
        {
            // StartAsync has just rescheduled every Watch that was due, so nothing is due before the first tick.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await StartDueRuns(stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    LogDispatchFailed(logger, error);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Lookout is stopping; let the Runs in progress finish or cancel first.
        }
        await Task.WhenAll(_runs.Keys);
    }

    private async Task RescheduleStaleRuns(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var now = timeProvider.GetUtcNow();
        var timeZone = schedules.TimeZone.Id;
        // Daily and cron times are wall-clock times, so they move when the install's time zone changes.
        var stale = await database.Watches
            .Where(watch => watch.NextRunAt == null || watch.NextRunAt <= now
                || (watch.Schedule.Kind != ScheduleKind.Interval && watch.NextRunTimeZone != timeZone))
            .ToListAsync(cancellationToken);
        foreach (var watch in stale)
        {
            watch.NextRunAt = schedules.NextRunAt(watch.Schedule, watch.JitterMinutes);
            watch.NextRunTimeZone = timeZone;
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task StartDueRuns(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var now = timeProvider.GetUtcNow();
        var due = await database.Watches.AsNoTracking()
            .Where(watch => watch.NextRunAt <= now)
            .OrderBy(watch => watch.NextRunAt)
            .ToListAsync(stoppingToken);

        // Decide which due Watches are already running before waiting for any slot. Otherwise a Watch's own Run
        // could finish while an earlier due Watch waits, turning a Run it should skip into a queued one.
        var queued = new Queue<Watch>();
        try
        {
            foreach (var watch in due)
            {
                if (runningWatches.TryStart(watch.Id))
                {
                    queued.Enqueue(watch);
                }
                else
                {
                    // A Watch that is already running skips this Run rather than overlapping it.
                    await Claim(database, watch, stoppingToken);
                }
            }

            while (queued.TryPeek(out var watch))
            {
                // Further due Runs wait here until a Run finishes.
                await runSlots.WaitAsync(stoppingToken);
                var started = false;
                try
                {
                    if (await Claim(database, watch, stoppingToken))
                    {
                        started = true;
                        Track(Task.Run(() => Run(watch.Id, stoppingToken), CancellationToken.None));
                    }
                }
                finally
                {
                    if (!started)
                    {
                        runSlots.Release();
                    }
                }
                queued.Dequeue();
                if (!started)
                {
                    runningWatches.Finish(watch.Id);
                }
            }
        }
        finally
        {
            // Stopping or a database error left these Watches unstarted.
            foreach (var watch in queued)
            {
                runningWatches.Finish(watch.Id);
            }
        }
    }

    /// <summary>
    /// Schedules the Watch's next Run from now, unless another backend instance has already claimed this one.
    /// </summary>
    private async Task<bool> Claim(LookoutDbContext database, Watch watch, CancellationToken cancellationToken)
    {
        var claimed = await database.Watches
            .Where(item => item.Id == watch.Id && item.NextRunAt == watch.NextRunAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.NextRunAt, schedules.NextRunAfterRunStarts(watch.Schedule, watch.JitterMinutes))
                .SetProperty(item => item.NextRunTimeZone, schedules.TimeZone.Id), cancellationToken);
        return claimed == 1;
    }

    private void Track(Task run)
    {
        _runs.TryAdd(run, 0);
        _ = run.ContinueWith(finished => _runs.TryRemove(finished, out _), TaskScheduler.Default);
    }

    private async Task Run(Guid watchId, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var execution = await scope.ServiceProvider.GetRequiredService<RunExecutor>().ExecuteAsync(watchId, stoppingToken);
            if (execution.Status != RunExecutionStatus.Recorded)
            {
                LogRunNotRecorded(logger, watchId, execution.Status);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Lookout stopped before the Run finished, so there is nothing to record.
        }
        catch (Exception error)
        {
            LogRunFailed(logger, error, watchId);
        }
        finally
        {
            runningWatches.Finish(watchId);
            runSlots.Release();
        }
    }

    [LoggerMessage(LogLevel.Error, "Could not start due Runs.")]
    private static partial void LogDispatchFailed(ILogger logger, Exception error);

    [LoggerMessage(LogLevel.Warning, "Scheduled Run of Watch {WatchId} was not recorded: {Status}.")]
    private static partial void LogRunNotRecorded(ILogger logger, Guid watchId, RunExecutionStatus status);

    [LoggerMessage(LogLevel.Error, "Scheduled Run of Watch {WatchId} failed.")]
    private static partial void LogRunFailed(ILogger logger, Exception error, Guid watchId);
}
