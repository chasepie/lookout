using System.Linq.Expressions;
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
    /// <summary>The Schedule, Jitter or time zone can't be used, as <see cref="ScheduleUpdate.Problem"/> explains.</summary>
    Invalid,
    /// <summary>
    /// The daily or cron times were given in a time zone that is no longer the install's, as
    /// <see cref="ScheduleUpdate.Problem"/> explains.
    /// </summary>
    TimeZoneChanged
}

public sealed record ScheduleUpdate(ScheduleUpdateStatus Status, string? Problem = null);

/// <summary>A Watch's scheduled Run that was due when read, which only a claim made with this same reading can start.</summary>
public sealed record DueRun(Guid WatchId, DateTimeOffset NextRunAt, Schedule Schedule, int JitterMinutes);

/// <summary>
/// Decides when Watches run, in the install's single time zone, and owns everything that does: the time zone, a Watch's
/// Schedule, Jitter, whether it is Paused, its next Run and when its last scheduled Run started. Each change made while
/// Lookout serves is checked against what was read, the time zone included, so changes made at the same time, such as
/// the scheduler claiming a Run while the user changes the Schedule or the time zone, can't undo one another.
/// Rescheduling at startup isn't, as it finishes before Lookout serves. A paused Watch has no next Run until it is
/// resumed.
/// </summary>
public sealed class WatchSchedules(IServiceScopeFactory scopes, TimeProvider timeProvider, TimeZoneInfo? configuredTimeZone)
{
    public const int MaximumJitterMinutes = 60;

    /// <summary>How many times an update reads the Watch again after it changed underneath, before giving up.</summary>
    private const int Attempts = 3;

    /// <summary>The longest time zone ID a change accepts, longer than any IANA time zone.</summary>
    private const int MaximumTimeZoneLength = 100;

    /// <summary>
    /// The time zone an install without one saved starts with: the configured one (<c>Scheduling:TimeZone</c>), or the
    /// host's local one (set by <c>TZ</c> in a container), as its IANA time zone, so it is one Settings can save. A host
    /// time zone without one, such as a custom one, is read as UTC.
    /// </summary>
    private TimeZoneInfo InitialTimeZone =>
        WithIanaId(configuredTimeZone ?? timeProvider.LocalTimeZone) ?? TimeZoneInfo.Utc;

    /// <summary>
    /// The IANA time zone that <paramref name="zone"/> is, such as America/New_York for Eastern Standard Time, or null
    /// if it has none this host can find again by its ID. The host's own time zone may not be: when .NET can't name it,
    /// its ID is <c>Local</c>, which saved would be refused by Settings and unknown to scheduling.
    /// </summary>
    public static TimeZoneInfo? WithIanaId(TimeZoneInfo zone)
    {
        if (CanBeSaved(zone))
        {
            return zone;
        }
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId)
            && TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out var ianaZone)
            && CanBeSaved(ianaZone)
                ? ianaZone
                : null;
    }

    private static bool CanBeSaved(TimeZoneInfo zone) => zone.HasIanaId && FindSaved(zone.Id) is not null;

    /// <summary>
    /// Saves the initial time zone as the install's, unless it already has one, which then outlasts any change to
    /// configuration. Run at startup, before Lookout serves.
    /// </summary>
    public async Task InitializeTimeZoneAsync(LookoutDbContext database, CancellationToken cancellationToken)
    {
        var initial = InitialTimeZone.Id;
        await database.Settings
            .Where(settings => settings.TimeZone == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(settings => settings.TimeZone, initial), cancellationToken);
    }

    /// <summary>
    /// Makes the IANA time zone the install's, and schedules each daily and cron Watch's next Run afresh from now in it,
    /// without catching up and still clear of its last Run. Interval Watches keep their next Run, and Paused Watches stay
    /// unscheduled until they are resumed in the new time zone.
    /// </summary>
    public async Task<ScheduleUpdate> ChangeTimeZoneAsync(string? timeZoneId, CancellationToken cancellationToken)
    {
        var id = timeZoneId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return new ScheduleUpdate(ScheduleUpdateStatus.Invalid, "Enter a time zone, such as Europe/London.");
        }

        if (id.Length > MaximumTimeZoneLength
            || !TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
            || !CanBeSaved(zone))
        {
            var shown = id.Length > MaximumTimeZoneLength ? id[..MaximumTimeZoneLength] + "…" : id;
            return new ScheduleUpdate(ScheduleUpdateStatus.Invalid,
                $"Lookout doesn't know a time zone called '{shown}'. Enter an IANA time zone, such as Europe/London.");
        }

        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        // SQLite's transactions are immediate, so nothing else is written between saving the time zone and rescheduling
        // for it. Writes made meanwhile check that the time zone they used is still the install's, and if not try again.
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        if (await ReadSavedTimeZoneIdAsync(database, cancellationToken) == zone.Id)
        {
            return new ScheduleUpdate(ScheduleUpdateStatus.Unchanged);
        }

        await database.Settings.ExecuteUpdateAsync(setters => setters
            .SetProperty(settings => settings.TimeZone, zone.Id), cancellationToken);

        // Daily and cron times are wall-clock times, so they move with the time zone.
        await RescheduleAsync(database, new SavedTimeZone(zone.Id, zone),
            watch => !watch.IsPaused && watch.Schedule.Kind != ScheduleKind.Interval, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ScheduleUpdate(ScheduleUpdateStatus.Changed);
    }

    /// <summary>
    /// Schedules the first Run of a Watch not yet saved, from its Schedule and Jitter, or explains why they can't be used.
    /// Call it in the transaction that saves the Watch, which holds off a change of time zone until it commits.
    /// A daily or cron Schedule whose times were given in <paramref name="shownTimeZone"/>, as the app's form names it, is
    /// refused if that is no longer the time zone they would run in, such as after a change made in another tab.
    /// </summary>
    public async Task<ScheduleUpdate> ScheduleFirstRunAsync(Watch watch, LookoutDbContext database, string? shownTimeZone,
        CancellationToken cancellationToken)
    {
        // Without one, a change of time zone could land before the Watch is saved, leaving it scheduled in the old one.
        if (database.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Schedule a new Watch's first Run in the transaction that saves it.");
        }

        var timeZone = await ReadTimeZoneAsync(database, cancellationToken);
        if (shownTimeZone is not null && watch.Schedule.Kind != ScheduleKind.Interval && shownTimeZone != timeZone.Zone.Id)
        {
            return new ScheduleUpdate(ScheduleUpdateStatus.TimeZoneChanged,
                $"Lookout's time zone changed to {timeZone.Zone.Id} while this Watch was being created, so it wasn't. " +
                $"Check its time, which is now in {timeZone.Zone.Id}, and create it again.");
        }

        if (Validate(timeZone, watch.Schedule, watch.JitterMinutes) is { } problem)
        {
            return new ScheduleUpdate(ScheduleUpdateStatus.Invalid, problem);
        }

        watch.NextRunAt = NextRunAt(timeZone, watch.Schedule, watch.JitterMinutes);
        watch.NextRunTimeZone = timeZone.Zone.Id;
        return new ScheduleUpdate(ScheduleUpdateStatus.Changed);
    }

    /// <summary>
    /// Replaces the Watch's Schedule and Jitter and schedules its next Run, still clear of its last, unless it is Paused.
    /// </summary>
    public async Task<ScheduleUpdate> ChangeScheduleAsync(Guid watchId, Schedule schedule, int jitterMinutes,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var timeZone = await ReadTimeZoneAsync(database, cancellationToken);
            if (Validate(timeZone, schedule, jitterMinutes) is { } problem)
            {
                return new ScheduleUpdate(ScheduleUpdateStatus.Invalid, problem);
            }

            var current = await database.Watches
                .Where(watch => watch.Id == watchId)
                .Select(watch => new { watch.LastScheduledRunAt })
                .SingleOrDefaultAsync(cancellationToken);
            if (current is null)
            {
                return new ScheduleUpdate(ScheduleUpdateStatus.NotFound);
            }

            // A set-based update leaves Revision alone, so a Run finishing meanwhile is still recorded. It applies
            // only if the scheduler started no Run since the read, as the next Run must stay clear of the last one,
            // and the time zone is still the one the next Run was worked out in.
            var nextRunAt = NextRunAt(timeZone, schedule, jitterMinutes, current.LastScheduledRunAt);
            var updated = await database.Watches
                .Where(watch =>
                    watch.Id == watchId
                    && watch.LastScheduledRunAt == current.LastScheduledRunAt)
                .Where(StillIn(database, timeZone))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(watch => watch.Schedule, schedule)
                    .SetProperty(watch => watch.JitterMinutes, jitterMinutes)
                    .SetProperty(watch => watch.NextRunAt, watch => watch.IsPaused ? null : nextRunAt)
                    .SetProperty(watch => watch.NextRunTimeZone, timeZone.Zone.Id), cancellationToken);
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
            var timeZone = await ReadTimeZoneAsync(database, cancellationToken);
            var current = await database.Watches.AsNoTracking()
                .Where(watch => watch.Id == watchId)
                .Select(watch => new
                {
                    watch.IsPaused,
                    watch.Schedule,
                    watch.JitterMinutes,
                    watch.LastScheduledRunAt
                })
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
            // still recorded. It applies only if the Schedule and time zone are still the ones read, as either saved
            // meanwhile decides the next Run.
            var nextRunAt = NextRunAt(timeZone, current.Schedule, current.JitterMinutes, current.LastScheduledRunAt);
            var updated = await database.Watches
                .Where(watch => watch.Id == watchId && watch.IsPaused
                    && watch.Schedule == current.Schedule
                    && watch.JitterMinutes == current.JitterMinutes
                    && watch.LastScheduledRunAt == current.LastScheduledRunAt)
                .Where(StillIn(database, timeZone))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(watch => watch.IsPaused, false)
                    .SetProperty(watch => watch.NextRunAt, nextRunAt)
                    .SetProperty(watch => watch.NextRunTimeZone, timeZone.Zone.Id), cancellationToken);
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
        return await database.Watches.AsNoTracking()
            .Where(watch => watch.NextRunAt <= now && !watch.IsPaused)
            .OrderBy(watch => watch.NextRunAt)
            .Select(watch => new DueRun(
                WatchId: watch.Id,
                NextRunAt: watch.NextRunAt!.Value,
                Schedule: watch.Schedule,
                JitterMinutes: watch.JitterMinutes))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Records the due Run as starting now and schedules the next one from now, unless the Watch was paused or
    /// rescheduled since it was read, or another backend instance has already claimed this Run. Nor is it claimed if the
    /// time zone changed while its next Run was worked out; an interval Watch, which the change leaves due, is then
    /// claimed by the scheduler's next look.
    /// </summary>
    public async Task<bool> ClaimAsync(DueRun run, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var timeZone = await ReadTimeZoneAsync(database, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var claimed = await database.Watches
            .Where(watch => watch.Id == run.WatchId && watch.NextRunAt == run.NextRunAt)
            .Where(StillIn(database, timeZone))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(watch => watch.NextRunAt, NextRunAt(timeZone, run.Schedule, run.JitterMinutes, now))
                .SetProperty(watch => watch.NextRunTimeZone, timeZone.Zone.Id)
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
        var timeZone = await ReadTimeZoneAsync(database, cancellationToken);
        var timeZoneId = timeZone.Zone.Id;

        // Daily and cron times are wall-clock times, so they move when the install's time zone changes.
        await RescheduleAsync(database, timeZone, watch =>
            !watch.IsPaused
            && (watch.NextRunAt == null
                || watch.NextRunAt <= now
                || (watch.Schedule.Kind != ScheduleKind.Interval && watch.NextRunTimeZone != timeZoneId)),
            cancellationToken);
    }

    /// <summary>
    /// Schedules each Watch that <paramref name="needsRescheduling"/> picks afresh from now in the time zone, without
    /// catching up and still clear of its last Run.
    /// </summary>
    private async Task RescheduleAsync(LookoutDbContext database, SavedTimeZone timeZone,
        Expression<Func<Watch, bool>> needsRescheduling, CancellationToken cancellationToken)
    {
        var watches = await database.Watches.Where(needsRescheduling).ToListAsync(cancellationToken);
        foreach (var watch in watches)
        {
            watch.NextRunAt = NextRunAt(timeZone, watch.Schedule, watch.JitterMinutes, watch.LastScheduledRunAt);
            watch.NextRunTimeZone = timeZone.Zone.Id;
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The install's saved time zone. One this host doesn't know, such as after its time zone data changed, is read as
    /// the initial time zone, while writes still check against the one saved.
    /// </summary>
    private async Task<SavedTimeZone> ReadTimeZoneAsync(LookoutDbContext database, CancellationToken cancellationToken) =>
        Resolve(await ReadSavedTimeZoneIdAsync(database, cancellationToken));

    private static Task<string?> ReadSavedTimeZoneIdAsync(LookoutDbContext database, CancellationToken cancellationToken) =>
        database.Settings.Select(settings => settings.TimeZone).SingleAsync(cancellationToken);

    /// <summary>
    /// The time zone daily and cron Schedules use when <paramref name="savedTimeZoneId"/> is the install's: that one, or
    /// the initial time zone when this host doesn't know it.
    /// </summary>
    public string EffectiveTimeZone(string? savedTimeZoneId) => Resolve(savedTimeZoneId).Zone.Id;

    /// <summary>
    /// Whether this host knows <paramref name="savedTimeZoneId"/>, so daily and cron Schedules run in it rather than
    /// the time zone they fall back to. The app asks this rather than comparing IDs, as lookups ignore case, so the
    /// two IDs can differ only in case.
    /// </summary>
    public static bool KnowsTimeZone(string? savedTimeZoneId) => FindSaved(savedTimeZoneId) is not null;

    private SavedTimeZone Resolve(string? id) => new(id, FindSaved(id) ?? InitialTimeZone);

    private static TimeZoneInfo? FindSaved(string? id) =>
        id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;

    /// <summary>Whether the install's time zone is still the one read, so a next Run worked out in it is current.</summary>
    private static Expression<Func<Watch, bool>> StillIn(LookoutDbContext database, SavedTimeZone timeZone)
    {
        var id = timeZone.Id;
        return watch => database.Settings.Any(settings => settings.TimeZone == id);
    }

    private string? Validate(SavedTimeZone timeZone, Schedule schedule, int jitterMinutes)
    {
        if (schedule.GetInvalidReason(timeProvider.GetUtcNow(), timeZone.Zone) is { } reason)
        {
            return reason;
        }

        return jitterMinutes is < 0 or > MaximumJitterMinutes
            ? $"Jitter must be between 0 and {MaximumJitterMinutes} minutes."
            : null;
    }

    /// <summary>
    /// The schedule's next occurrence after now, delayed by up to <paramref name="jitterMinutes"/>, in whole seconds.
    /// Jitter can delay a Run until just before the schedule's next occurrence, so the next Run is also held at least
    /// <see cref="Schedule.MinimumInterval"/> after <paramref name="lastRunAt"/>.
    /// </summary>
    private DateTimeOffset? NextRunAt(
        SavedTimeZone timeZone,
        Schedule schedule,
        int jitterMinutes,
        DateTimeOffset? lastRunAt = null)
    {
        var jitter = TimeSpan.FromSeconds(Random.Shared.Next(jitterMinutes * 60 + 1));
        var next = schedule.NextOccurrence(timeProvider.GetUtcNow(), timeZone.Zone) + jitter;
        var earliest = lastRunAt + Schedule.MinimumInterval;
        if (next < earliest)
        {
            next = earliest;
        }

        // The database keeps a tenth of a millisecond, so rounding keeps a new Watch's response the same as its record.
        return next is { } at ? RoundUpToWholeSecond(at) : null;
    }

    /// <summary>
    /// Rounds up rather than down, so a Run held <see cref="Schedule.MinimumInterval"/> after the last one stays clear of
    /// it.
    /// </summary>
    private static DateTimeOffset RoundUpToWholeSecond(DateTimeOffset time)
    {
        var seconds = (time.UtcTicks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
        return new DateTimeOffset(seconds * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    /// <summary>The install's time zone as saved, which a write checks is still current, and the zone it names.</summary>
    private sealed record SavedTimeZone(string? Id, TimeZoneInfo Zone);
}
