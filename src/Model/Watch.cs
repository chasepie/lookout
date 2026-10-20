namespace ChasePie.Lookout.Model;

public sealed class Watch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string NotificationUrl { get; set; }
    public Guid? CurrentScriptVersionId { get; set; }
    public ScriptVersion? CurrentScriptVersion { get; set; }
    public string? Baseline { get; set; }
    public required Schedule Schedule { get; set; }
    /// <summary>Up to this many minutes of random delay added to each scheduled Run.</summary>
    public int JitterMinutes { get; set; }
    /// <summary>When the scheduler next runs this Watch, including its jitter.</summary>
    public DateTimeOffset? NextRunAt { get; set; }
    /// <summary>The time zone <see cref="NextRunAt"/> was worked out in, so a restart in another one can redo it.</summary>
    public string? NextRunTimeZone { get; set; }
    /// <summary>Advanced by every recorded Run, so a Run evaluated against a stale Baseline can't be saved.</summary>
    public long Revision { get; set; }
    public ICollection<ScriptVersion> ScriptVersions { get; set; } = [];
    public ICollection<Run> Runs { get; set; } = [];
}
