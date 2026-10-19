using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Runner;

public interface IRunnerClient
{
    Task<RunnerResult> ExecuteAsync(RunnerRequest request, CancellationToken cancellationToken);
}

public sealed record RunnerRequest(string Source, string FileKind, ScriptContext Context, int TimeoutMs = 120_000);
public sealed record ScriptContext(string NotificationUrl);

public sealed record RunnerResult
{
    public required RunOutcome Outcome { get; init; }
    public string? Content { get; init; }
    public string? Details { get; init; }
    public string? ErrorMessage { get; init; }
    public required double DurationMs { get; init; }
}
