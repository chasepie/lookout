using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.UnitTests;

/// <summary>
/// Which event, if any, a finished run shows, given the watch's earlier runs.
/// </summary>
[TestClass]
public sealed class WatchEventsTests
{
    private const int Threshold = 3;

    [TestMethod]
    public void ASuccessWithDifferentContentIsAContentChange()
    {
        var watchEvent = Detect(Success("Sold Out"), Success("In Stock"));

        Assert.AreEqual(WatchEvent.ContentChange, watchEvent);
    }

    [TestMethod]
    public void AKnownFailureBelowTheThresholdShowsNothing()
    {
        var watchEvent = Detect(Success("In Stock"), KnownFailure("Blocked"));

        Assert.IsNull(watchEvent);
    }

    [TestMethod]
    public void TheKnownFailureThatReachesTheThresholdIsAKnownFailureStreak()
    {
        var watchEvent = Detect(Success("In Stock"), KnownFailure("Blocked"), KnownFailure("Blocked"), KnownFailure("Blocked"));

        Assert.AreEqual(WatchEvent.KnownFailureStreak, watchEvent);
    }

    [TestMethod]
    public void AKnownFailureStreakPastTheThresholdShowsNothingMore()
    {
        var watchEvent = Detect(
            KnownFailure("Blocked"), KnownFailure("Blocked"), KnownFailure("Blocked"), KnownFailure("Blocked"));

        Assert.IsNull(watchEvent);
    }

    [TestMethod]
    public void CrashesNeitherAddToNorEndAKnownFailureStreak()
    {
        var watchEvent = Detect(
            Success("In Stock"), KnownFailure("Blocked"), Crash("Timeout"), KnownFailure("Blocked"), Crash("Timeout"),
            KnownFailure("Blocked"));

        Assert.AreEqual(WatchEvent.KnownFailureStreak, watchEvent);
    }

    [TestMethod]
    public void TheFirstCrashIsACrash()
    {
        var watchEvent = Detect(Success("In Stock"), Crash("Error: No price"));

        Assert.AreEqual(WatchEvent.Crash, watchEvent);
    }

    [TestMethod]
    public void ASecondCrashInARowShowsNothing()
    {
        var watchEvent = Detect(Success("In Stock"), Crash("Error: No price"), Crash("Error: No price"));

        Assert.IsNull(watchEvent);
    }

    [TestMethod]
    public void TheSameContentAfterAKnownFailureStreakIsARecovery()
    {
        var watchEvent = Detect(
            Success("In Stock"), KnownFailure("Blocked"), KnownFailure("Blocked"), KnownFailure("Blocked"),
            Success("In Stock"));

        Assert.AreEqual(WatchEvent.Recovery, watchEvent);
    }

    [TestMethod]
    public void TheFirstSuccessAfterACrashStreakIsARecovery()
    {
        var watchEvent = Detect(
            Success("In Stock"), Crash("Error: No price"), KnownFailure("Blocked"), Success("In Stock"));

        Assert.AreEqual(WatchEvent.Recovery, watchEvent);
    }

    [TestMethod]
    public void ChangedContentAfterAStreakIsAContentChangeRatherThanARecovery()
    {
        var watchEvent = Detect(Success("Sold Out"), Crash("Error: No price"), Success("In Stock"));

        Assert.AreEqual(WatchEvent.ContentChange, watchEvent);
    }

    [TestMethod]
    public void ASuccessAfterAKnownFailureStreakBelowTheThresholdShowsNothing()
    {
        var watchEvent = Detect(Success("In Stock"), KnownFailure("Blocked"), KnownFailure("Blocked"), Success("In Stock"));

        Assert.IsNull(watchEvent);
    }

    [TestMethod]
    public void AWatchsFirstSuccessShowsNothing()
    {
        var watchEvent = Detect(KnownFailure("Blocked"), Success("In Stock"));

        Assert.IsNull(watchEvent);
    }

    [TestMethod]
    public void TheSameContentAgainShowsNothing()
    {
        var watchEvent = Detect(Success("In Stock"), Success("In Stock"));

        Assert.IsNull(watchEvent);
    }

    [TestMethod]
    public void AThresholdOfOneMakesTheFirstKnownFailureAStreak()
    {
        var watchEvent = DetectWithThreshold(1, Success("In Stock"), KnownFailure("Blocked"));

        Assert.AreEqual(WatchEvent.KnownFailureStreak, watchEvent);
    }

    [TestMethod]
    public void AnInterruptedRunShowsNothing()
    {
        var watchEvent = Detect(Success("In Stock"), Interrupted());

        Assert.IsNull(watchEvent);
    }

    [TestMethod]
    public void AnInterruptedRunDoesNotStartACrashStreak()
    {
        var watchEvent = Detect(Success("In Stock"), Interrupted(), Crash("Error: No price"));

        Assert.AreEqual(WatchEvent.Crash, watchEvent);
    }

    [TestMethod]
    public void AnInterruptedRunIsNotRecoveredFrom()
    {
        var watchEvent = Detect(Success("In Stock"), Interrupted(), Success("In Stock"));

        Assert.IsNull(watchEvent);
    }

    [TestMethod]
    public void LoweringTheThresholdBelowAStreakNotifiesOnItsNextKnownFailure()
    {
        var watchEvent = DetectInTurn(
            (Success("In Stock"), 5), (KnownFailure("Blocked"), 5), (KnownFailure("Blocked"), 5),
            (KnownFailure("Blocked"), 5), (KnownFailure("Blocked"), 3));

        Assert.AreEqual(WatchEvent.KnownFailureStreak, watchEvent);
    }

    [TestMethod]
    public void AStreakNotifiesOnlyOnceAfterItsThresholdIsLowered()
    {
        var watchEvent = DetectInTurn(
            (KnownFailure("Blocked"), 5), (KnownFailure("Blocked"), 5), (KnownFailure("Blocked"), 5),
            (KnownFailure("Blocked"), 3), (KnownFailure("Blocked"), 3));

        Assert.IsNull(watchEvent);
    }

    [TestMethod]
    public void AStreakThatNeverNotifiedIsNotRecoveredFrom()
    {
        // Lowered while the streak was already past it, but the streak ended before notifying
        var watchEvent = DetectInTurn(
            (Success("In Stock"), 5), (KnownFailure("Blocked"), 5), (KnownFailure("Blocked"), 5),
            (KnownFailure("Blocked"), 5), (Success("In Stock"), 2));

        Assert.IsNull(watchEvent);
    }

    [TestMethod]
    public void RaisingTheThresholdAfterAStreakNotifiedStillRecovers()
    {
        var watchEvent = DetectInTurn(
            (Success("In Stock"), 2), (KnownFailure("Blocked"), 2), (KnownFailure("Blocked"), 2),
            (Success("In Stock"), 5));

        Assert.AreEqual(WatchEvent.Recovery, watchEvent);
    }

    /// <summary>
    /// The event the last of <paramref name="runsOldestFirst"/> shows.
    /// </summary>
    private static WatchEvent? Detect(params WatchRun[] runsOldestFirst) => DetectWithThreshold(Threshold, runsOldestFirst);

    private static WatchEvent? DetectWithThreshold(int knownFailureThreshold, params WatchRun[] runsOldestFirst) =>
        DetectInTurn([.. runsOldestFirst.Select(run => (run, knownFailureThreshold))]);

    /// <summary>
    /// Works out each run's event in turn, with the threshold the watch had then, recording it on the run as the Backend
    /// does, and returns the last run's.
    /// </summary>
    private static WatchEvent? DetectInTurn(params (WatchRun Run, int Threshold)[] runsOldestFirst)
    {
        for (var i = 0; i < runsOldestFirst.Length; i++)
        {
            var (run, threshold) = runsOldestFirst[i];
            var earlierRunsNewestFirst = runsOldestFirst[..i].Select(r => r.Run).Reverse().ToList();
            run.Event = WatchEvents.Detect(run, earlierRunsNewestFirst, threshold);
        }

        return runsOldestFirst[^1].Run.Event;
    }

    private static WatchRun Success(string content) => Done(run => run.Content = content);

    private static WatchRun KnownFailure(string reason) => Done(run => run.FailureReason = reason);

    private static WatchRun Crash(string error) => Done(run => run.Error = error);

    private static WatchRun Interrupted() => Done(run =>
    {
        run.Error = "Interrupted because the Backend restarted.";
        run.Interrupted = true;
    });

    private static WatchRun Done(Action<WatchRun> setOutcome)
    {
        var run = new WatchRun { WatchId = 1, CreatedTime = DateTime.UtcNow, Status = WatchRunStatus.Done };
        setOutcome(run);
        return run;
    }
}
