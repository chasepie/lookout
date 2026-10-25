namespace ChasePie.Lookout.Backend.Controllers;

public sealed record ScriptVersionSummary(Guid Id, int Number, string FileKind, DateTimeOffset UploadedAt, bool IsCurrent);

public sealed record ScriptVersionSource(Guid Id, int Number, string FileKind, DateTimeOffset UploadedAt, bool IsCurrent,
    string Source);

public sealed class UploadScriptRequest
{
    public required IFormFile Script { get; init; }
    /// <summary>Keeps the Baseline and failure streak, for a new version that reports its content the same way.</summary>
    public bool KeepBaseline { get; init; }
}
