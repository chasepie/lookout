using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.Services;

/// <summary>
/// What decides when a watch next runs: when its latest run was created, and whether a run is waiting to start.
/// <see cref="WatchScheduler"/> and the API's watch summaries both read it through
/// <see cref="WatchScheduleStateQueries.WithScheduleState"/>, so what the API reports is what the scheduler does.
/// </summary>
public sealed class WatchScheduleState
{
    public required Watch Watch { get; init; }

    /// <summary>
    /// When the latest run was created, in UTC; <see langword="null"/> if the watch has never run.
    /// </summary>
    public DateTime? LastRunTime { get; init; }

    public bool HasQueuedRun { get; init; }

    /// <summary>
    /// When the scheduler next creates a run for the watch; see <see cref="Watch.GetDueTime"/>.
    /// </summary>
    public DateTime? DueTime(DateTime now) => Watch.GetDueTime(LastRunTime, HasQueuedRun, now, TimeZoneInfo.Local);
}

public static class WatchScheduleStateQueries
{
    /// <summary>
    /// Reads each watch with its schedule state, in the same database query.
    /// </summary>
    public static IQueryable<WatchScheduleState> WithScheduleState(this IQueryable<Watch> watches) =>
        watches.Select(w => new WatchScheduleState
        {
            Watch = w,
            LastRunTime = w.WatchRuns
                .OrderByDescending(wr => wr.CreatedTime)
                .Select(wr => (DateTime?)wr.CreatedTime)
                .FirstOrDefault(),
            HasQueuedRun = w.WatchRuns.Any(wr => wr.Status == WatchRunStatus.Created),
        });
}
