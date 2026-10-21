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
        (outcome, errorMessage) = RecordedOutcome(outcome, content, errorMessage);

        if (outcome != RunOutcome.Succeeded)
        {
            // A failure streak notifies once: on its first Errored Run, or on its third Known failure. Until it notifies,
            // a streak holds only Known failures, so its length is how many there have been in a row.
            var failureStreak = watch.FailureStreak + 1;

            Notification? failure;
            if (watch.FailureStreakNotified)
            {
                failure = null;
            }
            else if (outcome == RunOutcome.Errored)
            {
                failure = Notification.Errored(watch.Name, errorMessage, watch.NotificationUrl);
            }
            else if (failureStreak >= Notification.KnownFailuresBeforeNotifying)
            {
                failure = Notification.KnownFailures(watch.Name, details, watch.NotificationUrl);
            }
            else
            {
                failure = null;
            }

            return new RunEvaluation(
                Baseline: watch.Baseline,
                FailureStreak: failureStreak,
                FailureStreakNotified: watch.FailureStreakNotified || failure is not null,
                IsChange: false,
                Outcome: outcome,
                ErrorMessage: errorMessage,
                Notifications: failure is null ? [] : [failure]);
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
        return new RunEvaluation(
            Baseline: content,
            FailureStreak: 0,
            FailureStreakNotified: false,
            IsChange: isChange,
            Outcome: outcome,
            ErrorMessage: errorMessage,
            Notifications: notifications);
    }

    /// <summary>
    /// The outcome a Run records for a runner's result, which Test runs show too: a Succeeded result without content is
    /// Errored, since there is nothing to compare with the Baseline.
    /// </summary>
    public static (RunOutcome Outcome, string? ErrorMessage) RecordedOutcome(RunOutcome outcome, string? content,
        string? errorMessage) =>
        outcome == RunOutcome.Succeeded && content is null
            ? (RunOutcome.Errored, "Succeeded runner result did not include content.")
            : (outcome, errorMessage);
}
