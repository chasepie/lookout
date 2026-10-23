using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Scheduling;

public enum ScheduleUpdateStatus
{
    Changed,
    /// <summary>The Watch was already as asked, such as pausing a Paused Watch.</summary>
    Unchanged,
    NotFound,
    /// <summary>The Watch kept changing underneath the update, which then gave up.</summary>
    KeptChanging,
    /// <summary>The Schedule or Jitter can't be used, as <see cref="ScheduleUpdate.Problem"/> explains.</summary>
    Invalid
}

public sealed record ScheduleUpdate(ScheduleUpdateStatus Status, string? Problem = null);

/// <summary>A Watch's scheduled Run that was due when read, which only a claim made with this same reading can start.</summary>
public sealed record DueRun(Guid WatchId, DateTimeOffset NextRunAt, Schedule Schedule, int JitterMinutes);

/// <summary>
/// Decides when Watches run, in the install's single time zone, and owns everything that does: a Watch's Schedule,
/// Jitter, whether it is Paused, its next Run and when its last scheduled Run started. Every change is checked against
/// what was read, so changes made at the same time, such as the scheduler claiming a Run while the user changes the
/// Schedule, can't undo one another. A paused Watch has no next Run until it is resumed.
/// </summary>
public sealed class WatchSchedules(IServiceScopeFactory scopes, TimeProvider timeProvider, TimeZoneInfo? configuredTimeZone)
{
    public const int MaximumJitterMinutes = 60;

    /// <summary>How many times an update reads the Watch again after it changed underneath, before giving up.</summary>
    private const int Attempts = 3;

    /// <summary>The configured time zone, or the host's local one (set by <c>TZ</c> in a container).</summary>
    private readonly TimeZoneInfo _timeZone = configuredTimeZone ?? timeProvider.LocalTimeZone;

    /// <summary>
    /// Schedules the first Run of a Watch not yet saved, from its Schedule and Jitter, or explains why they can't be used.
    /// </summary>
    public string? Schedule(Watch watch)
    {
        if (Validate(watch.Schedule, watch.JitterMinutes) is { } problem)
        {
            return problem;
        }
        watch.NextRunAt = NextRunAt(watch.Schedule, watch.JitterMinutes);
        watch.NextRunTimeZone = _timeZone.Id;
        return null;
    }

    /// <summary>
    /// Replaces the Watch's Schedule and Jitter and schedules its next Run, still clear of its last, unless it is Paused.
    /// </summary>
    public async Task<ScheduleUpdate> ChangeScheduleAsync(Guid watchId, Schedule schedule, int jitterMinutes,
        CancellationToken cancellationToken)
    {
        if (Validate(schedule, jitterMinutes) is { } problem)
        {
            return new ScheduleUpdate(ScheduleUpdateStatus.Invalid, problem);
        }

        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var current = await database.Watches
                .Where(watch => watch.Id == watchId)
                .Select(watch => new { watch.LastScheduledRunAt })
                .SingleOrDefaultAsync(cancellationToken);
            if (current is null)
            {
                return new ScheduleUpdate(ScheduleUpdateStatus.NotFound);
            }

            // A set-based update leaves Revision alone, so a Run finishing meanwhile is still recorded. It applies
            // only if the scheduler started no Run since the read, as the next Run must stay clear of the last one.
            var nextRunAt = NextRunAt(schedule, jitterMinutes, current.LastScheduledRunAt);
            var updated = await database.Watches
                .Where(watch => watch.Id == watchId && watch.LastScheduledRunAt == current.LastScheduledRunAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(watch => watch.Schedule, schedule)
                    .SetProperty(watch => watch.JitterMinutes, jitterMinutes)
                    .SetProperty(watch => watch.NextRunAt, watch => watch.IsPaused ? null : nextRunAt)
                    .SetProperty(watch => watch.NextRunTimeZone, _timeZone.Id), cancellationToken);
            if (updated == 1)
            {
                return new ScheduleUpdate(ScheduleUpdateStatus.Changed);
            }
        }
        return new ScheduleUpdate(ScheduleUpdateStatus.KeptChanging);
    }

    /// <summary>Stops the Watch's scheduled Runs, keeping its Schedule for when it is resumed.</summary>
    public async Task<ScheduleUpdate> PauseAsync(Guid watchId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        // Clearing the next Run also stops the scheduler claiming one it read as due just before.
        var updated = await database.Watches
            .Where(watch => watch.Id == watchId && !watch.IsPaused)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(watch => watch.IsPaused, true)
                .SetProperty(watch => watch.NextRunAt, (DateTimeOffset?)null), cancellationToken);
        if (updated == 1)
        {
            return new ScheduleUpdate(ScheduleUpdateStatus.Changed);
        }
        return await database.Watches.AnyAsync(watch => watch.Id == watchId, cancellationToken)
            ? new ScheduleUpdate(ScheduleUpdateStatus.Unchanged)
            : new ScheduleUpdate(ScheduleUpdateStatus.NotFound);
    }

    /// <summary>
    /// Starts the Watch's scheduled Runs again, the next one scheduled from now, so a Watch resumed after a long pause
    /// doesn't run at once.
    /// </summary>
    public async Task<ScheduleUpdate> ResumeAsync(Guid watchId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var current = await database.Watches.AsNoTracking()
                .Where(watch => watch.Id == watchId)
                .Select(watch => new { watch.IsPaused, watch.Schedule, watch.JitterMinutes, watch.LastScheduledRunAt })
                .SingleOrDefaultAsync(cancellationToken);
            if (current is null)
            {
                return new ScheduleUpdate(ScheduleUpdateStatus.NotFound);
            }

            if (!current.IsPaused)
            {
                return new ScheduleUpdate(ScheduleUpdateStatus.Unchanged);
            }

            // The failure streak carries on, and a set-based update leaves the Revision alone, so a Run in progress is
            // still recorded. It applies only if the Schedule is still the one read, as one saved meanwhile decides the
            // next Run.
            var nextRunAt = NextRunAt(current.Schedule, current.JitterMinutes, current.LastScheduledRunAt);
            var updated = await database.Watches
                .Where(watch => watch.Id == watchId && watch.IsPaused
                    && watch.Schedule == current.Schedule
                    && watch.JitterMinutes == current.JitterMinutes
                    && watch.LastScheduledRunAt == current.LastScheduledRunAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(watch => watch.IsPaused, false)
                    .SetProperty(watch => watch.NextRunAt, nextRunAt)
                    .SetProperty(watch => watch.NextRunTimeZone, _timeZone.Id), cancellationToken);
            if (updated == 1)
            {
                return new ScheduleUpdate(ScheduleUpdateStatus.Changed);
            }
        }
        return new ScheduleUpdate(ScheduleUpdateStatus.KeptChanging);
    }

    /// <summary>The scheduled Runs due now, in the order they fell due.</summary>
    public async Task<IReadOnlyList<DueRun>> DueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var now = timeProvider.GetUtcNow();
        var due = await database.Watches.AsNoTracking()
            .Where(watch => watch.NextRunAt <= now && !watch.IsPaused)
            .OrderBy(watch => watch.NextRunAt)
            .ToListAsync(cancellationToken);
        return due.ConvertAll(watch => new DueRun(watch.Id, watch.NextRunAt!.Value, watch.Schedule, watch.JitterMinutes));
    }

    /// <summary>
    /// Records the due Run as starting now and schedules the next one from now, unless the Watch was paused or
    /// rescheduled since it was read, or another backend instance has already claimed this Run.
    /// </summary>
    public async Task<bool> ClaimAsync(DueRun run, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var now = timeProvider.GetUtcNow();
        var claimed = await database.Watches
            .Where(watch => watch.Id == run.WatchId && watch.NextRunAt == run.NextRunAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(watch => watch.NextRunAt, NextRunAt(run.Schedule, run.JitterMinutes, now))
                .SetProperty(watch => watch.NextRunTimeZone, _timeZone.Id)
                .SetProperty(watch => watch.LastScheduledRunAt, now), cancellationToken);
        return claimed == 1;
    }

    /// <summary>
    /// Schedules afresh each Watch that fell due while Lookout was down, so it doesn't catch up, and each daily and cron
    /// Watch scheduled in another time zone.
    /// </summary>
    public async Task RescheduleStaleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var now = timeProvider.GetUtcNow();
        var timeZone = _timeZone.Id;

        // Daily and cron times are wall-clock times, so they move when the install's time zone changes.
        var stale = await database.Watches
            .Where(watch => !watch.IsPaused)
            .Where(watch =>
                watch.NextRunAt == null
                || watch.NextRunAt <= now
                || (watch.Schedule.Kind != ScheduleKind.Interval && watch.NextRunTimeZone != timeZone))
            .ToListAsync(cancellationToken);
        foreach (var watch in stale)
        {
            watch.NextRunAt = NextRunAt(watch.Schedule, watch.JitterMinutes, watch.LastScheduledRunAt);
            watch.NextRunTimeZone = timeZone;
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private string? Validate(Schedule schedule, int jitterMinutes) =>
        schedule.Validate(timeProvider.GetUtcNow(), _timeZone)
        ?? (jitterMinutes is < 0 or > MaximumJitterMinutes
            ? $"Jitter must be between 0 and {MaximumJitterMinutes} minutes."
            : null);

    /// <summary>
    /// The schedule's next occurrence after now, delayed by up to <paramref name="jitterMinutes"/>, in whole seconds.
    /// Jitter can delay a Run until just before the schedule's next occurrence, so the next Run is also held at least
    /// <see cref="Model.Schedule.MinimumInterval"/> after <paramref name="lastRunAt"/>.
    /// </summary>
    private DateTimeOffset? NextRunAt(Schedule schedule, int jitterMinutes, DateTimeOffset? lastRunAt = null)
    {
        var jitter = TimeSpan.FromSeconds(Random.Shared.Next(jitterMinutes * 60 + 1));
        var next = schedule.NextOccurrence(timeProvider.GetUtcNow(), _timeZone) + jitter;
        var earliest = lastRunAt + Model.Schedule.MinimumInterval;
        if (next < earliest)
        {
            next = earliest;
        }

        // The database keeps a tenth of a millisecond, so rounding keeps a new Watch's response the same as its record.
        return next is { } at ? RoundUpToWholeSecond(at) : null;
    }

    /// <summary>
    /// Rounds up rather than down, so a Run held <see cref="Model.Schedule.MinimumInterval"/> after the last one stays
    /// clear of it.
    /// </summary>
    private static DateTimeOffset RoundUpToWholeSecond(DateTimeOffset time)
    {
        var seconds = (time.UtcTicks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
        return new DateTimeOffset(seconds * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }
}
