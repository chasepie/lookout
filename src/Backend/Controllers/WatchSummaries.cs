using System.Linq.Expressions;
using ChasePie.Lookout.Core;
using ChasePie.Lookout.Backend.Services;
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
    string? FailureReason,
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
        FailureReason: run.FailureReason,
        Error: run.Error,
        HasScreenshot: run.Screenshot != null);
}

/// <summary>
/// A watch with its latest runs, newest first, and when its schedule next creates a run.
/// </summary>
/// <param name="RecentRuns">
/// At most <see cref="RecentRunCount"/> runs, newest first: as many as were asked for.
/// </param>
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
    bool ScreenshotOnSuccess,
    bool ScreenshotOnKnownFailure,
    bool ScreenshotOnCrash,
    int KnownFailureThreshold,
    string? ScriptFileName,
    IReadOnlyList<WatchRunSummary> RecentRuns,
    DateTime? NextRunTime)
{
    /// <summary>
    /// How many runs <see cref="RecentRuns"/> holds at most: enough to show a watch's recent history at a glance.
    /// </summary>
    public const int RecentRunCount = 24;

    /// <summary>
    /// Reads watches with up to <paramref name="recentRunCount"/> of their latest runs, in one database query, without
    /// tracking them.
    /// </summary>
    public static async Task<List<WatchSummary>> ListAsync(
        IQueryable<Watch> watches, int recentRunCount, DateTime now, CancellationToken cancellationToken)
    {
        var rows = await watches
            .AsNoTracking()
            .WithScheduleState()
            .Select(state => new
            {
                State = state,
                RecentRuns = state.Watch.WatchRuns
                    .AsQueryable()
                    .OrderByDescending(wr => wr.Id)
                    .Take(recentRunCount)
                    .Select(WatchRunSummary.Projection)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);
        return rows.ConvertAll(row => new WatchSummary(
            Id: row.State.Watch.Id,
            Name: row.State.Watch.Name,
            ScheduleEnabled: row.State.Watch.ScheduleEnabled,
            ScheduleKind: row.State.Watch.ScheduleKind,
            ScheduleInterval: row.State.Watch.ScheduleInterval,
            UseVpn: row.State.Watch.UseVpn,
            ScreenshotOnSuccess: row.State.Watch.ScreenshotOnSuccess,
            ScreenshotOnKnownFailure: row.State.Watch.ScreenshotOnKnownFailure,
            ScreenshotOnCrash: row.State.Watch.ScreenshotOnCrash,
            KnownFailureThreshold: row.State.Watch.KnownFailureThreshold,
            ScriptFileName: row.State.Watch.ScriptFileName,
            RecentRuns: row.RecentRuns,
            NextRunTime: row.State.DueTime(now)));
    }
}
