using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Cronos;

namespace ChasePie.Lookout.Core;

public enum WatchScheduleKind
{
    Minutely,
    Hourly,
    Daily,
    Weekly,
    Cron
}

public class CreateWatchDto : IValidatableObject
{
    [Required]
    public required string Name { get; set; }

    public bool ScheduleEnabled { get; set; }
    public WatchScheduleKind? ScheduleKind { get; set; }
    public string? ScheduleInterval { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!ScheduleEnabled)
        {
            yield break;
        }

        if (ScheduleKind is null)
        {
            yield return new ValidationResult(
                $"{nameof(ScheduleKind)} is required when {nameof(ScheduleEnabled)} is true.",
                [nameof(ScheduleKind)]);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(ScheduleInterval))
        {
            yield return new ValidationResult(
                $"{nameof(ScheduleInterval)} is required when {nameof(ScheduleEnabled)} is true.",
                [nameof(ScheduleInterval)]);
            yield break;
        }

        switch (ScheduleKind)
        {
            case WatchScheduleKind.Minutely:
            case WatchScheduleKind.Hourly:
            case WatchScheduleKind.Daily:
            case WatchScheduleKind.Weekly:
                if (!int.TryParse(ScheduleInterval, CultureInfo.InvariantCulture, out var interval) || interval <= 0)
                {
                    yield return new ValidationResult(
                        $"{nameof(ScheduleInterval)} must be a positive integer for {ScheduleKind} schedule.",
                        [nameof(ScheduleInterval)]);
                }
                break;
            case WatchScheduleKind.Cron:
                if (!CronExpression.TryParse(ScheduleInterval, CronFormat.Standard, out _))
                {
                    yield return new ValidationResult(
                        $"{nameof(ScheduleInterval)} must be a valid cron expression for {ScheduleKind} schedule.",
                        [nameof(ScheduleInterval)]);
                }
                break;
        }
    }
}

public class Watch : CreateWatchDto
{
    public int Id { get; set; }

    /// <summary>
    /// The uploaded script's original file name.
    /// </summary>
    public string? ScriptFileName { get; set; }

    /// <summary>
    /// Identifies the stored script file; see <see cref="WatchScriptsDirectory.GetStoredFileName"/>.
    /// </summary>
    public Guid? ScriptFileId { get; set; }

    public ICollection<WatchRun> WatchRuns { get; set; } = [];

    public Watch() { }

    [SetsRequiredMembers]
    public Watch(CreateWatchDto dto)
    {
        Name = dto.Name;
        Update(dto);
    }

    public void Update<T>(T dto) where T : CreateWatchDto
    {
        Name = dto.Name;
        ScheduleEnabled = dto.ScheduleEnabled;
        ScheduleKind = dto.ScheduleKind;
        ScheduleInterval = dto.ScheduleInterval;
    }

    /// <summary>
    /// When the next scheduled run is due, given when the previous run was created.
    /// </summary>
    /// <param name="lastRunTime">When the previous run was created, in UTC.</param>
    /// <param name="timeZone">The time zone cron expressions are evaluated in.</param>
    /// <returns>
    /// The due time in UTC, or <see langword="null"/> if the schedule is disabled, invalid,
    /// or a cron expression with no further occurrences.
    /// </returns>
    public DateTime? GetNextRunTime(DateTime lastRunTime, TimeZoneInfo timeZone)
    {
        if (!ScheduleEnabled || string.IsNullOrWhiteSpace(ScheduleInterval))
        {
            return null;
        }

        var lastRunUtc = DateTime.SpecifyKind(lastRunTime, DateTimeKind.Utc);
        if (ScheduleKind == WatchScheduleKind.Cron)
        {
            return CronExpression.TryParse(ScheduleInterval, CronFormat.Standard, out var cron)
                ? cron.GetNextOccurrence(lastRunUtc, timeZone)
                : null;
        }

        if (!int.TryParse(ScheduleInterval, CultureInfo.InvariantCulture, out var count) || count <= 0)
        {
            return null;
        }

        TimeSpan? interval = ScheduleKind switch
        {
            WatchScheduleKind.Minutely => TimeSpan.FromMinutes(count),
            WatchScheduleKind.Hourly => TimeSpan.FromHours(count),
            WatchScheduleKind.Daily => TimeSpan.FromDays(count),
            WatchScheduleKind.Weekly => TimeSpan.FromDays(7 * count),
            _ => null,
        };
        return lastRunUtc + interval;
    }
}
