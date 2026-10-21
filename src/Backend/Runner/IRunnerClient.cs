using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Runner;

public interface IRunnerClient
{
    Task<RunnerResult> ExecuteAsync(RunnerRequest request, CancellationToken cancellationToken);
}

/// <param name="AlwaysScreenshot">Screenshots a Succeeded Script's page too, as a Test run shows; otherwise only others'.</param>
public sealed record RunnerRequest(string Source, string FileKind, ScriptContext Context, int TimeoutMs = 120_000,
    bool AlwaysScreenshot = false);
public sealed record ScriptContext(string NotificationUrl);

public sealed record RunnerResult
{
    public required RunOutcome Outcome { get; init; }
    public string? Content { get; init; }
    public string? Details { get; init; }
    public string? ErrorMessage { get; init; }
    public required double DurationMs { get; init; }
    /// <summary>A base64 PNG of the Script's page, for a Run that didn't Succeed or one that asked for it.</summary>
    public string? Screenshot { get; init; }
    /// <summary>What the Script wrote to stdout and stderr.</summary>
    public string? ConsoleOutput { get; init; }
}
