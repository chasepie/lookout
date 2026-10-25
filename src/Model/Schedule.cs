using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;

namespace ChasePie.Lookout.Model;

[JsonConverter(typeof(ScheduleKindJsonConverter))]
public enum ScheduleKind
{
    /// <summary>Every N minutes, hours or days, counted from the previous Run.</summary>
    Interval,
    /// <summary>Once a day at a time of day in the install's time zone.</summary>
    Daily,
    /// <summary>A five-field cron expression in the install's time zone.</summary>
    Cron
}

public sealed class ScheduleKindJsonConverter() : JsonStringEnumConverter<ScheduleKind>(JsonNamingPolicy.CamelCase, false);

[JsonConverter(typeof(IntervalUnitJsonConverter))]
public enum IntervalUnit
{
    Minutes,
    Hours,
    Days
}

public sealed class IntervalUnitJsonConverter() : JsonStringEnumConverter<IntervalUnit>(JsonNamingPolicy.CamelCase, false);

/// <summary>When a Watch runs. Only the fields its <see cref="Kind"/> uses are set.</summary>
public sealed record Schedule
{
    /// <summary>Runs closer together than this could hammer a site or get its IP banned.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan MaximumInterval = TimeSpan.FromDays(365);

    /// <summary>How many upcoming cron occurrences are checked against <see cref="MinimumInterval"/>.</summary>
    private const int CheckedOccurrences = 100;

    private const string MinimumIntervalMessage = "Schedules must not run more than once every 3 minutes.";

    public required ScheduleKind Kind { get; init; }
    public int? Every { get; init; }
    public IntervalUnit? Unit { get; init; }
    public TimeOnly? TimeOfDay { get; init; }
    public string? Cron { get; init; }

    public static Schedule DailyAt(TimeOnly timeOfDay) => new()
    {
        Kind = ScheduleKind.Daily,
        TimeOfDay = new TimeOnly(timeOfDay.Hour, timeOfDay.Minute)
    };

    public static Schedule FromCron(string cron) => new()
    {
        Kind = ScheduleKind.Cron,
        Cron = cron.Trim()
    };

    /// <summary>Explains why this schedule can't be used, or returns null when it can.</summary>
    public string? GetInvalidReason(DateTimeOffset now, TimeZoneInfo timeZone)
    {
        string? ValidateInterval()
        {
            if (Every is not { } every || Unit is not { } unit)
            {
                return "An interval schedule needs a number and a unit of minutes, hours or days.";
            }

            if (every < 1)
            {
                return MinimumIntervalMessage;
            }

            // A year of minutes is the most any unit allows; checking it first keeps the length from overflowing.
            const int minutesInYear = 60 * 24 * 365;
            if (every > minutesInYear || IntervalLength(every, unit) > MaximumInterval)
            {
                return "An interval schedule must run at least once every 365 days.";
            }
            return IntervalLength(every, unit) < MinimumInterval ? MinimumIntervalMessage : null;
        }

        string? ValidateCron()
        {
            if (string.IsNullOrWhiteSpace(Cron) || Cron.Length > 200
                || !CronExpression.TryParse(Cron, CronFormat.Standard, out var expression))
            {
                return "Enter a cron expression with five fields: minute, hour, day of month, month and day of week.";
            }

            var previous = expression.GetNextOccurrence(now, timeZone);
            if (previous is null)
            {
                return "This cron expression never runs.";
            }

            for (var i = 1; i < CheckedOccurrences; i++)
            {
                var next = expression.GetNextOccurrence(previous.Value, timeZone);
                if (next is null)
                {
                    break;
                }
                if (next.Value - previous.Value < MinimumInterval)
                {
                    return MinimumIntervalMessage;
                }
                previous = next;
            }
            return null;
        }

        return Kind switch
        {
            ScheduleKind.Interval => ValidateInterval(),
            ScheduleKind.Daily => TimeOfDay is null ? "A daily schedule needs a time of day." : null,
            ScheduleKind.Cron => ValidateCron(),
            _ => "Choose an interval, daily or cron schedule.",
        };
    }

    /// <summary>The first time after <paramref name="now"/> this valid schedule runs, in UTC, or null if it never runs again.</summary>
    public DateTimeOffset? NextOccurrence(DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var occurrence = Kind switch
        {
            ScheduleKind.Interval => now + IntervalLength(Every!.Value, Unit!.Value),
            ScheduleKind.Daily => CronExpression.Parse($"{TimeOfDay!.Value.Minute} {TimeOfDay.Value.Hour} * * *")
                .GetNextOccurrence(now, timeZone),
            ScheduleKind.Cron => CronExpression.Parse(Cron!).GetNextOccurrence(now, timeZone),
            _ => throw new InvalidOperationException($"Unknown schedule kind {Kind}.")
        };
        return occurrence?.ToUniversalTime();
    }

    private static TimeSpan IntervalLength(int every, IntervalUnit unit) => unit switch
    {
        IntervalUnit.Minutes => TimeSpan.FromMinutes(every),
        IntervalUnit.Hours => TimeSpan.FromHours(every),
        IntervalUnit.Days => TimeSpan.FromDays(every),
        _ => throw new InvalidOperationException($"Unknown interval unit {unit}.")
    };
}
