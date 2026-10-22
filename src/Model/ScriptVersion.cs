namespace ChasePie.Lookout.Model;

public sealed class ScriptVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WatchId { get; set; }
    /// <summary>Counts the Watch's Script versions from 1, in the order they were uploaded.</summary>
    public int Number { get; set; }
    /// <summary>When the version was uploaded; for one uploaded before Lookout recorded this, its first Run.</summary>
    public DateTimeOffset UploadedAt { get; set; }
    public required string Source { get; set; }
    public required string FileKind { get; set; }
}
