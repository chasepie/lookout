using System.Linq.Expressions;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

/// <summary>
/// A run without its screenshot, which is only sent with the single run. Lists of runs stay small this way.
/// </summary>
/// <param name="HasScreenshot">Whether the run captured a screenshot; fetch the run to see it.</param>
public sealed record WatchRunSummary(
    int Id,
    int WatchId,
    DateTime CreatedTime,
    DateTime? StartTime,
    DateTime? EndTime,
    WatchRunStatus Status,
    string? Content,
    string? Error,
    bool HasScreenshot)
{
    /// <summary>
    /// Selects a summary in the database query, so the screenshot's bytes are never read.
    /// </summary>
    public static readonly Expression<Func<WatchRun, WatchRunSummary>> Projection = run => new WatchRunSummary(
        Id: run.Id,
        WatchId: run.WatchId,
        CreatedTime: run.CreatedTime,
        StartTime: run.StartTime,
        EndTime: run.EndTime,
        Status: run.Status,
        Content: run.Content,
        Error: run.Error,
        HasScreenshot: run.Screenshot != null);
}

/// <summary>
/// A watch with its latest runs, newest first, and when its schedule next creates a run.
/// </summary>
/// <param name="RecentRuns">At most <see cref="RecentRunCount"/> runs, newest first.</param>
/// <param name="NextRunTime">
/// When the schedule next creates a run, in UTC, from <see cref="Watch.GetDueTime"/>; <see langword="null"/> if the
/// watch isn't scheduled, has no script, or has a run waiting to start. A time in the past means any moment now.
/// </param>
public sealed record WatchSummary(
    int Id,
    string Name,
    bool ScheduleEnabled,
    WatchScheduleKind? ScheduleKind,
    string? ScheduleInterval,
    bool UseVpn,
    string? ScriptFileName,
    IReadOnlyList<WatchRunSummary> RecentRuns,
    DateTime? NextRunTime)
{
    /// <summary>
    /// How many runs <see cref="RecentRuns"/> holds at most: enough to show a watch's recent history at a glance.
    /// </summary>
    public const int RecentRunCount = 24;

    /// <summary>
    /// Reads watches with their recent runs in one database query, without tracking them.
    /// </summary>
    public static async Task<List<WatchSummary>> ListAsync(
        IQueryable<Watch> watches, DateTime now, CancellationToken cancellationToken)
    {
        var rows = await watches
            .AsNoTracking()
            .Select(w => new
            {
                Watch = w,
                RecentRuns = w.WatchRuns
                    .AsQueryable()
                    .OrderByDescending(wr => wr.Id)
                    .Take(RecentRunCount)
                    .Select(WatchRunSummary.Projection)
                    .ToList(),
                // As WatchScheduler reads them, so NextRunTime is what it will do
                LastRunTime = w.WatchRuns
                    .OrderByDescending(wr => wr.CreatedTime)
                    .Select(wr => (DateTime?)wr.CreatedTime)
                    .FirstOrDefault(),
                HasQueuedRun = w.WatchRuns.Any(wr => wr.Status == WatchRunStatus.Created),
            })
            .ToListAsync(cancellationToken);
        return rows.ConvertAll(row => new WatchSummary(
            Id: row.Watch.Id,
            Name: row.Watch.Name,
            ScheduleEnabled: row.Watch.ScheduleEnabled,
            ScheduleKind: row.Watch.ScheduleKind,
            ScheduleInterval: row.Watch.ScheduleInterval,
            UseVpn: row.Watch.UseVpn,
            ScriptFileName: row.Watch.ScriptFileName,
            RecentRuns: row.RecentRuns,
            NextRunTime: row.Watch.GetDueTime(row.LastRunTime, row.HasQueuedRun, now, TimeZoneInfo.Local)));
    }
}
