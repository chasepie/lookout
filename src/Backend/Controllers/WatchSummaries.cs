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
/// When the schedule is next due, in UTC; <see langword="null"/> if the watch isn't scheduled or has no script.
/// A time in the past means the run is about to be created.
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
    /// Reads watches with their recent runs in one database query.
    /// </summary>
    public static async Task<List<WatchSummary>> ListAsync(
        IQueryable<Watch> watches, DateTime now, CancellationToken cancellationToken)
    {
        var rows = await watches
            .Select(w => new
            {
                Watch = w,
                RecentRuns = w.WatchRuns
                    .AsQueryable()
                    .OrderByDescending(wr => wr.Id)
                    .Take(RecentRunCount)
                    .Select(WatchRunSummary.Projection)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);
        return rows.ConvertAll(row => From(row.Watch, row.RecentRuns, now));
    }

    private static WatchSummary From(Watch watch, List<WatchRunSummary> recentRuns, DateTime now) => new(
        Id: watch.Id,
        Name: watch.Name,
        ScheduleEnabled: watch.ScheduleEnabled,
        ScheduleKind: watch.ScheduleKind,
        ScheduleInterval: watch.ScheduleInterval,
        UseVpn: watch.UseVpn,
        ScriptFileName: watch.ScriptFileName,
        RecentRuns: recentRuns,
        NextRunTime: NextRunTimeOf(watch, recentRuns, now));

    /// <summary>
    /// When <see cref="Services.WatchScheduler"/> next creates a run: counting from the latest run, or now if there's
    /// none.
    /// </summary>
    private static DateTime? NextRunTimeOf(Watch watch, List<WatchRunSummary> recentRuns, DateTime now)
    {
        if (!watch.ScheduleEnabled || watch.ScriptFileId is null)
        {
            return null;
        }

        return recentRuns.Count > 0
            ? watch.GetNextRunTime(recentRuns.Max(r => r.CreatedTime), TimeZoneInfo.Local)
            : now;
    }
}
