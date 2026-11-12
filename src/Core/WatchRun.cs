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
    /// Whether the run's known failure asked for a failover. Recorded even when Lookout couldn't make one.
    /// </summary>
    public bool AskedForFailover { get; set; }

    /// <summary>
    /// The VPN's region while the run ran: the region the Backend last switched it to. <see langword="null"/> for a run
    /// of a watch that doesn't use the VPN, while the VPN isn't configured, or before its first switch.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// The run this one retries, when Lookout queued it after a failover.
    /// </summary>
    public int? RetryOfRunId { get; set; }

    /// <summary>
    /// Why the run crashed: what the script threw, or why it couldn't run or finish. Runs from before known failures
    /// were kept apart have their reasons here too.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Whether the run crashed because the Backend stopped while it ran, rather than because of its script.
    /// Such runs show no events, and are skipped when working out the events of later ones.
    /// </summary>
    public bool Interrupted { get; set; }

    /// <summary>
    /// The event the run showed, worked out when it finished from the watch's earlier runs and the threshold it had
    /// then. Kept so a later run can tell whether a known-failure streak has already been notified.
    /// </summary>
    public WatchEvent? Event { get; set; }
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
