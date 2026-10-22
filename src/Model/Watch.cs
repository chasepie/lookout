namespace ChasePie.Lookout.Model;

public sealed class Watch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string NotificationUrl { get; set; }
    public Guid? CurrentScriptVersionId { get; set; }
    public ScriptVersion? CurrentScriptVersion { get; set; }
    public string? Baseline { get; set; }
    /// <summary>How many Known failure or Errored Runs in a row the Watch has had since its last Succeeded Run.</summary>
    public int FailureStreak { get; set; }
    /// <summary>Whether the user has stopped the Watch's scheduled Runs. Run now still works.</summary>
    public bool IsPaused { get; set; }
    /// <summary>Whether the Watch's Runs go through the VPN rather than direct.</summary>
    public bool UsesVpn { get; set; }
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
    public const int DefaultTimeoutSeconds = 120;
    public const int MaxTimeoutSeconds = 600;
    public required Schedule Schedule { get; set; }
    /// <summary>Up to this many minutes of random delay added to each scheduled Run.</summary>
    public int JitterMinutes { get; set; }
    /// <summary>When the scheduler next runs this Watch, including its jitter.</summary>
    public DateTimeOffset? NextRunAt { get; set; }
    /// <summary>The time zone <see cref="NextRunAt"/> was worked out in, so a restart in another one can redo it.</summary>
    public string? NextRunTimeZone { get; set; }
    /// <summary>When the scheduler last started a Run of this Watch, which the next Run must stay clear of.</summary>
    public DateTimeOffset? LastScheduledRunAt { get; set; }
    /// <summary>Advanced by every recorded Run, so a Run evaluated against a stale Baseline can't be saved.</summary>
    public long Revision { get; set; }
    /// <summary>Whether the current failure streak has notified, so its end notifies that the Watch recovered.</summary>
    public bool FailureStreakNotified { get; set; }
    /// <summary>The Channels this Watch notifies.</summary>
    public ICollection<Channel> Channels { get; set; } = [];
    public ICollection<ScriptVersion> ScriptVersions { get; set; } = [];
    public ICollection<Run> Runs { get; set; } = [];
}
