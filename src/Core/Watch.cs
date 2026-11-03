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
    public string? ScriptFileName { get; set; }
    public Guid? ScriptFileId { get; set; }

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
}
