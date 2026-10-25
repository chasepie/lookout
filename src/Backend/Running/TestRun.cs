using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Running;

/// <summary>The result of a Test run, which, unlike a Run's, has a screenshot even when it Succeeded.</summary>
public sealed record TestRun(
    RunOutcome Outcome,
    string? Content,
    string? Details,
    string? ErrorMessage,
    double DurationMs,
    string? Screenshot,
    string? ConsoleOutput);
