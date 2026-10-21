namespace ChasePie.Lookout.Model;

public sealed class ScriptVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WatchId { get; set; }
    /// <summary>Counts the Watch's Script versions from 1, in the order they were uploaded.</summary>
    public int Number { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public required string Source { get; set; }
    public required string FileKind { get; set; }
}
