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

    /// <summary>
    /// The event the last of <paramref name="runsOldestFirst"/> shows.
    /// </summary>
    private static WatchEvent? Detect(params WatchRun[] runsOldestFirst) => DetectWithThreshold(Threshold, runsOldestFirst);

    private static WatchEvent? DetectWithThreshold(int knownFailureThreshold, params WatchRun[] runsOldestFirst)
    {
        var run = runsOldestFirst[^1];
        var earlierRunsNewestFirst = runsOldestFirst[..^1].Reverse().ToList();
        return WatchEvents.Detect(run, earlierRunsNewestFirst, knownFailureThreshold);
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
