using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Services;

/// <summary>
/// Creates runs for watches whose schedule is due, for <see cref="WatchRunWorker"/> to execute.
/// A watch that already has a run waiting to start is skipped until that run starts.
/// </summary>
/// <remarks>
/// A schedule counts from the watch's latest run, manual or scheduled. A watch that has never run
/// is due immediately. Cron expressions are evaluated in the Backend's local time zone.
/// Due watches are found by polling every <see cref="PollInterval"/>, so a run can be created up to
/// that long after it was due. Interval schedules count from that later time and drift slightly;
/// cron schedules don't.
/// </remarks>
public partial class WatchScheduler(
    IServiceScopeFactory scopeFactory,
    UpdatesNotifier notifier,
    ILogger<WatchScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await QueueDueRunsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogScheduleFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Creates a run for each scheduled watch that is due and has no run waiting to start.
    /// </summary>
    private async Task QueueDueRunsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();

        var scheduledWatches = await dbContext.Watches
            .Where(w => w.ScheduleEnabled && w.ScriptFileId != null)
            .Select(w => new
            {
                Watch = w,
                LastRunTime = w.WatchRuns
                    .OrderByDescending(wr => wr.CreatedTime)
                    .Select(wr => (DateTime?)wr.CreatedTime)
                    .FirstOrDefault(),
                HasQueuedRun = w.WatchRuns.Any(wr => wr.Status == WatchRunStatus.Created),
            })
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var createdRuns = new List<WatchRun>();
        foreach (var scheduled in scheduledWatches)
        {
            if (scheduled.HasQueuedRun)
            {
                continue;
            }

            var dueTime = scheduled.LastRunTime is { } lastRunTime
                ? scheduled.Watch.GetNextRunTime(lastRunTime, TimeZoneInfo.Local)
                : now;
            if (dueTime <= now)
            {
                var watchRun = new WatchRun(scheduled.Watch) { CreatedTime = now };
                dbContext.WatchRuns.Add(watchRun);
                createdRuns.Add(watchRun);
                LogRunScheduled(scheduled.Watch.Id);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (var watchRun in createdRuns)
        {
            await notifier.RunUpdatedAsync(watchRun);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled a run for watch {WatchId}")]
    private partial void LogRunScheduled(int watchId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to schedule watch runs")]
    private partial void LogScheduleFailed(Exception exception);
}
