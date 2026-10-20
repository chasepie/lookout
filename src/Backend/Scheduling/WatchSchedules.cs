using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Scheduling;

/// <summary>Works out when Watches run, in the install's single time zone.</summary>
public sealed class WatchSchedules(TimeProvider timeProvider, TimeZoneInfo? configuredTimeZone)
{
    public const int MaximumJitterMinutes = 60;

    /// <summary>The configured time zone, or the host's local one (set by <c>TZ</c> in a container).</summary>
    public TimeZoneInfo TimeZone { get; } = configuredTimeZone ?? timeProvider.LocalTimeZone;

    public string? Validate(Schedule schedule) => schedule.Validate(timeProvider.GetUtcNow(), TimeZone);

    /// <summary>
    /// The schedule's next occurrence after now, delayed by up to <paramref name="jitterMinutes"/>, in whole seconds.
    /// Jitter can delay a Run until just before the schedule's next occurrence, so the next Run is also held at least
    /// <see cref="Schedule.MinimumInterval"/> after <paramref name="lastRunAt"/>.
    /// </summary>
    public DateTimeOffset? NextRunAt(Schedule schedule, int jitterMinutes, DateTimeOffset? lastRunAt = null)
    {
        var jitter = TimeSpan.FromSeconds(Random.Shared.Next(jitterMinutes * 60 + 1));
        var next = schedule.NextOccurrence(timeProvider.GetUtcNow(), TimeZone) + jitter;
        var earliest = lastRunAt + Schedule.MinimumInterval;
        if (next < earliest)
        {
            next = earliest;
        }

        // The database keeps a tenth of a millisecond, so rounding keeps a new Watch's response the same as its record.
        return next is { } at ? RoundUpToWholeSecond(at) : null;
    }

    /// <summary>
    /// Rounds up rather than down, so a Run held <see cref="Schedule.MinimumInterval"/> after the last one stays clear of it.
    /// </summary>
    private static DateTimeOffset RoundUpToWholeSecond(DateTimeOffset time)
    {
        var seconds = (time.UtcTicks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
        return new DateTimeOffset(seconds * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }
}
