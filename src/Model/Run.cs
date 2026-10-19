namespace ChasePie.Lookout.Model;

public enum RunOutcome
{
    Succeeded,
    KnownFailure,
    Errored
}

public sealed class Run
{
    public long Id { get; set; }
    public Guid WatchId { get; set; }
    public Guid ScriptVersionId { get; set; }
    public RunOutcome Outcome { get; set; }
    public string? Content { get; set; }
    public string? Details { get; set; }
    public string? ErrorMessage { get; set; }
    public double DurationMs { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool IsChange { get; set; }
}
