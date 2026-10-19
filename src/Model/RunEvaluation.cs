namespace ChasePie.Lookout.Model;

/// <summary>The state changes produced by a Run, independent of storage and execution.</summary>
public sealed record RunEvaluation(string? Baseline, bool IsChange)
{
    public static RunEvaluation Evaluate(string? baseline, RunOutcome outcome, string? content)
    {
        if (outcome != RunOutcome.Succeeded)
        {
            return new RunEvaluation(baseline, false);
        }
        ArgumentNullException.ThrowIfNull(content);
        return new RunEvaluation(content, baseline is not null
            && !string.Equals(baseline.Trim(), content.Trim(), StringComparison.Ordinal));
    }
}
