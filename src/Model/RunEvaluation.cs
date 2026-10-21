namespace ChasePie.Lookout.Model;

/// <summary>The state changes and notifications produced by a Run, independent of storage and execution.</summary>
public sealed record RunEvaluation(
    string? Baseline,
    int FailureStreak,
    bool FailureStreakNotified,
    bool IsChange,
    RunOutcome Outcome,
    string? ErrorMessage,
    IReadOnlyList<Notification> Notifications)
{
    public static RunEvaluation Evaluate(Watch watch, RunOutcome outcome, string? content, string? details, string? errorMessage)
    {
        if (outcome == RunOutcome.Succeeded && content is null)
        {
            outcome = RunOutcome.Errored;
            errorMessage = "Succeeded runner result did not include content.";
        }

        if (outcome != RunOutcome.Succeeded)
        {
            // A failure streak notifies once: on its first Errored Run, or on its third Known failure. Until it notifies,
            // a streak holds only Known failures, so its length is how many there have been in a row.
            var failureStreak = watch.FailureStreak + 1;
            Notification? failure = watch.FailureStreakNotified ? null
                : outcome == RunOutcome.Errored ? Notification.Errored(watch.Name, errorMessage, watch.NotificationUrl)
                : failureStreak >= Notification.KnownFailuresBeforeNotifying
                    ? Notification.KnownFailures(watch.Name, details, watch.NotificationUrl)
                : null;
            return new RunEvaluation(watch.Baseline, failureStreak, watch.FailureStreakNotified || failure is not null,
                false, outcome, errorMessage, failure is null ? [] : [failure]);
        }

        var isChange = watch.Baseline is not null
            && !string.Equals(watch.Baseline.Trim(), content!.Trim(), StringComparison.Ordinal);
        List<Notification> notifications = [];
        // Setting the Baseline never notifies, even after a failure streak that did.
        if (watch.FailureStreakNotified && watch.Baseline is not null)
        {
            notifications.Add(Notification.Recovered(watch.Name, content!, watch.NotificationUrl));
        }
        if (isChange)
        {
            notifications.Add(Notification.Change(watch.Name, watch.Baseline!, content!, watch.NotificationUrl));
        }
        return new RunEvaluation(content, 0, false, isChange, outcome, errorMessage, notifications);
    }
}
