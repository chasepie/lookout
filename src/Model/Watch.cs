namespace ChasePie.Lookout.Model;

public sealed class Watch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string NotificationUrl { get; set; }
    public Guid? CurrentScriptVersionId { get; set; }
    public ScriptVersion? CurrentScriptVersion { get; set; }
    public string? Baseline { get; set; }
    /// <summary>Advanced by every recorded Run, so a Run evaluated against a stale Baseline can't be saved.</summary>
    public long Revision { get; set; }
    public ICollection<ScriptVersion> ScriptVersions { get; set; } = [];
    public ICollection<Run> Runs { get; set; } = [];
}
