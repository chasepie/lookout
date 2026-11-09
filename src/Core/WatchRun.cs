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
    /// <summary>
    /// What the script found, when it returned a success.
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// The script's reason, when it returned a known failure.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Why the run crashed: what the script threw, or why it couldn't run or finish. Runs from before known failures
    /// were kept apart have their reasons here too.
    /// </summary>
    public string? Error { get; set; }
    public byte[]? Screenshot { get; set; }

    /// <summary>
    /// <see cref="Screenshot"/>'s type, <c>image/png</c> or <c>image/jpeg</c>, when the run has one.
    /// </summary>
    public string? ScreenshotMediaType { get; set; }

    public WatchRun() { }

    [SetsRequiredMembers]
    public WatchRun(Watch watch)
    {
        WatchId = watch.Id;
        CreatedTime = DateTime.UtcNow;
        Status = WatchRunStatus.Created;
    }
}
