using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;
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

    /// <summary>
    /// Whether runs send the browser's traffic through the VPN proxy (<c>GLUETUN_PROXY_URL</c>).
    /// </summary>
    public bool UseVpn { get; set; }

    /// <summary>
    /// Whether a run that ends in a success gets a full-page screenshot, when the script returned none of its own.
    /// </summary>
    public bool ScreenshotOnSuccess { get; set; }

    /// <summary>
    /// Whether a run that ends in a known failure gets a full-page screenshot, when the script returned none of its own.
    /// </summary>
    public bool ScreenshotOnKnownFailure { get; set; }

    /// <summary>
    /// Whether a run whose script throws gets a full-page screenshot.
    /// </summary>
    public bool ScreenshotOnCrash { get; set; }

    /// <summary>
    /// When the watch's runs get a screenshot the script didn't return.
    /// </summary>
    public ScreenshotSettings GetScreenshotSettings() =>
        new(ScreenshotOnSuccess, ScreenshotOnKnownFailure, ScreenshotOnCrash);

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

    /// <summary>
    /// Left out of the JSON: the API sends a watch's runs as <c>WatchRunSummary</c>s, without their screenshots.
    /// </summary>
    [JsonIgnore]
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
        UseVpn = dto.UseVpn;
        ScreenshotOnSuccess = dto.ScreenshotOnSuccess;
        ScreenshotOnKnownFailure = dto.ScreenshotOnKnownFailure;
        ScreenshotOnCrash = dto.ScreenshotOnCrash;
    }

    /// <summary>
    /// When the scheduler next creates a run for this watch: counting from the latest run, or right away if it has
    /// never run. The scheduler and the API both go by this, so what the API reports is what the scheduler does.
    /// </summary>
    /// <param name="lastRunTime">When the latest run was created, in UTC, or <see langword="null"/> if none.</param>
    /// <param name="hasQueuedRun">
    /// Whether a run is waiting to start. The scheduler creates no other until it does, so nothing is due meanwhile.
    /// </param>
    /// <param name="now">The current time, in UTC.</param>
    /// <param name="timeZone">The time zone cron expressions are evaluated in.</param>
    /// <returns>
    /// The due time in UTC, which may be past; <see langword="null"/> if the schedule is off, the watch has no script,
    /// a run is queued, or the schedule has no further occurrences.
    /// </returns>
    public DateTime? GetDueTime(DateTime? lastRunTime, bool hasQueuedRun, DateTime now, TimeZoneInfo timeZone)
    {
        if (!ScheduleEnabled || ScriptFileId is null || hasQueuedRun)
        {
            return null;
        }

        return lastRunTime is { } last ? GetNextRunTime(last, timeZone) : now;
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
