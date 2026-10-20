namespace ChasePie.Lookout.Model;

/// <summary>The state changes produced by a Run, independent of storage and execution.</summary>
public sealed record RunEvaluation(string? Baseline, int FailureStreak, bool IsChange, RunOutcome Outcome, string? ErrorMessage)
{
    public static RunEvaluation Evaluate(string? baseline, int failureStreak, RunOutcome outcome, string? content,
        string? errorMessage)
    {
        if (outcome != RunOutcome.Succeeded)
        {
            return new RunEvaluation(baseline, failureStreak + 1, false, outcome, errorMessage);
        }

        if (content is null)
        {
            return new RunEvaluation(baseline, failureStreak + 1, false, RunOutcome.Errored,
                "Succeeded runner result did not include content.");
        }

        var isChange = baseline is not null
            && !string.Equals(baseline.Trim(), content.Trim(), StringComparison.Ordinal);
        return new RunEvaluation(content, 0, isChange, outcome, errorMessage);
    }
}
