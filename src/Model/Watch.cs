using System.Globalization;

namespace ChasePie.Lookout.Model;

public enum WatchScheduleKind
{
    Minutely,
    Hourly,
    Daily,
    Weekly,
    Cron
}

public class Watch
{
    public int Id { get; set; }
    public required string Name { get; set; }

    public bool ScheduleEnabled { get; set; }
    public WatchScheduleKind? ScheduleKind { get; set; }
    public string? ScheduleInterval { get; set; }

    public ICollection<WatchRun> WatchRuns { get; set; } = [];
}
