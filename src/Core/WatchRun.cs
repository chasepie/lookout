using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace ChasePie.Lookout.Core;

public enum WatchRunStatus
{
    Created,
    Running,
    Done
}

public class WatchRun
{
    public int Id { get; set; }

    public required int WatchId { get; set; }

    [JsonIgnore]
    public Watch Watch { get; set; } = null!;

    public required DateTime CreatedTime { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }

    public required WatchRunStatus Status { get; set; }
    public string? Content { get; set; }
    public string? Error { get; set; }
    public byte[]? Screenshot { get; set; }

    public WatchRun() { }

    [SetsRequiredMembers]
    public WatchRun(Watch watch)
    {
        WatchId = watch.Id;
        CreatedTime = DateTime.UtcNow;
        Status = WatchRunStatus.Created;
    }
}
