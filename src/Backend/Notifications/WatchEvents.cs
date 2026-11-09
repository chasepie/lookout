using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>
/// Works out which <see cref="WatchEvent"/> a finished run shows, from the watch's earlier runs as they stand.
/// </summary>
public static class WatchEvents
{
    /// <param name="run">The run that just finished.</param>
    /// <param name="earlierRunsNewestFirst">The watch's runs before <paramref name="run"/>, newest first.</param>
    /// <param name="knownFailureThreshold">How many known failures in a row make a known-failure streak worth notifying.</param>
    /// <returns>The event, or <see langword="null"/> if the run shows none.</returns>
    public static WatchEvent? Detect(
        WatchRun run, IReadOnlyList<WatchRun> earlierRunsNewestFirst, int knownFailureThreshold)
    {
        if (run.Interrupted)
        {
            return null;
        }

        // Interrupted runs show nothing about the watch, so they neither start nor end a streak
        earlierRunsNewestFirst = [.. earlierRunsNewestFirst.Where(r => !r.Interrupted)];

        if (IsKnownFailure(run))
        {
            // Crashes neither add to the streak nor end it
            var streak = 1 + earlierRunsNewestFirst
                .Where(r => !IsCrash(r))
                .TakeWhile(IsKnownFailure)
                .Count();
            return streak == knownFailureThreshold ? WatchEvent.KnownFailureStreak : null;
        }

        if (IsCrash(run))
        {
            return earlierRunsNewestFirst is [var previous, ..] && IsCrash(previous) ? null : WatchEvent.Crash;
        }

        if (!IsSuccess(run))
        {
            return null;
        }

        var previousSuccess = earlierRunsNewestFirst.FirstOrDefault(IsSuccess);
        if (previousSuccess is not null && previousSuccess.Content != run.Content)
        {
            return WatchEvent.ContentChange;
        }

        var sincePreviousSuccess = earlierRunsNewestFirst.TakeWhile(r => !IsSuccess(r)).ToList();
        var recovered = sincePreviousSuccess.Any(IsCrash)
            || sincePreviousSuccess.Count(IsKnownFailure) >= knownFailureThreshold;
        return recovered ? WatchEvent.Recovery : null;
    }

    private static bool IsCrash(WatchRun run) => run.Error is not null;

    private static bool IsKnownFailure(WatchRun run) => !IsCrash(run) && run.FailureReason is not null;

    private static bool IsSuccess(WatchRun run) => !IsCrash(run) && run.Content is not null;
}
