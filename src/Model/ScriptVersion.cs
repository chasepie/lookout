namespace ChasePie.Lookout.Model;

public sealed class ScriptVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WatchId { get; set; }
    public required string Source { get; set; }
    public required string FileKind { get; set; }
}
