using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Scheduling;

/// <summary>Works out when Watches run, in the install's single time zone.</summary>
public sealed class WatchSchedules(TimeProvider timeProvider, TimeZoneInfo? configuredTimeZone)
{
    public const int MaximumJitterMinutes = 60;

    /// <summary>The configured time zone, or the host's local one (set by <c>TZ</c> in a container).</summary>
    public TimeZoneInfo TimeZone { get; } = configuredTimeZone ?? timeProvider.LocalTimeZone;

    public string? Validate(Schedule schedule) => schedule.Validate(timeProvider.GetUtcNow(), TimeZone);

    /// <summary>The schedule's next occurrence after now, delayed by up to <paramref name="jitterMinutes"/>.</summary>
    public DateTimeOffset? NextRunAt(Schedule schedule, int jitterMinutes)
    {
        var jitter = TimeSpan.FromSeconds(Random.Shared.Next(jitterMinutes * 60 + 1));
        return schedule.NextOccurrence(timeProvider.GetUtcNow(), TimeZone) + jitter;
    }

    /// <summary>
    /// When to run a Watch whose Run is starting now. Jitter can delay a Run until just before the schedule's next
    /// occurrence, so the next Run is held back to keep Runs at least <see cref="Schedule.MinimumInterval"/> apart.
    /// </summary>
    public DateTimeOffset? NextRunAfterRunStarts(Schedule schedule, int jitterMinutes)
    {
        var earliest = timeProvider.GetUtcNow() + Schedule.MinimumInterval;
        var next = NextRunAt(schedule, jitterMinutes);
        return next < earliest ? earliest : next;
    }
}
