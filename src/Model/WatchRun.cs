using System;

namespace ChasePie.Lookout.Model;

public enum WatchRunStatus
{
    Created,
    Running,
    Done
}

public class WatchRun
{
    public int Id { get; set; }

    public int WatchId { get; set; }
    public Watch Watch { get; set; } = null!;

    public DateTime CreatedTime { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }

    public WatchRunStatus Status { get; set; }
    public string? Content { get; set; }
    public string? Error { get; set; }
    public byte[]? Screenshot { get; set; }
}
