using ChasePie.Lookout.Backend.VpnServices;

namespace ChasePie.Lookout.Backend.UnitTests;

/// <summary>
/// How VPN runs share <see cref="VpnGate"/>, and region switches take it to themselves.
/// </summary>
[TestClass]
public sealed class VpnGateTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void RunsShareTheGate()
    {
        var gate = new VpnGate();

        using var first = gate.TryEnterRun();
        using var second = gate.TryEnterRun();

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(2, gate.Runs);
        Assert.IsFalse(gate.HoldsUpRuns);
    }

    [TestMethod]
    public void ASwitchWaitsForEveryRunInProgress()
    {
        var gate = new VpnGate();
        var first = gate.TryEnterRun()!;
        var second = gate.TryEnterRun()!;
        using var ticket = gate.QueueSwitch();

        var waiting = ticket.WaitAsync(TestContext.CancellationToken);
        first.Dispose();
        Assert.IsFalse(waiting.IsCompleted);
        second.Dispose();

        Assert.IsTrue(waiting.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task AQueuedSwitchKeepsNewRunsOutUntilItsDone()
    {
        var gate = new VpnGate();
        var run = gate.TryEnterRun()!;
        var ticket = gate.QueueSwitch();

        // Before the switch even waits, so a run that asks for a failover keeps others out once it lets go
        Assert.IsTrue(gate.HoldsUpRuns);
        Assert.IsNull(gate.TryEnterRun());
        run.Dispose();
        await ticket.WaitAsync(TestContext.CancellationToken);
        Assert.IsNull(gate.TryEnterRun());
        ticket.Dispose();

        using var after = gate.TryEnterRun();
        Assert.IsNotNull(after);
    }

    [TestMethod]
    public async Task ASwitchNotYetWaitingDoesntHoldUpOneThatIs()
    {
        // As a failover queued by a run waits for the switch ahead of it to finish before it waits for the gate
        var gate = new VpnGate();
        using var failover = gate.QueueSwitch();
        using var rotation = gate.QueueSwitch();

        await rotation.WaitAsync(TestContext.CancellationToken);
        rotation.Dispose();
        Assert.IsNull(gate.TryEnterRun(), "The failover is still queued.");
        await failover.WaitAsync(TestContext.CancellationToken);
        failover.Dispose();

        using var run = gate.TryEnterRun();
        Assert.IsNotNull(run);
    }

    [TestMethod]
    public async Task SwitchesHaveTheGateOneAtATimeInTheOrderTheyWaited()
    {
        var gate = new VpnGate();
        var first = gate.QueueSwitch();
        using var second = gate.QueueSwitch();

        await first.WaitAsync(TestContext.CancellationToken);
        var secondWaiting = second.WaitAsync(TestContext.CancellationToken);
        Assert.IsFalse(secondWaiting.IsCompleted);
        first.Dispose();

        await secondWaiting;
    }

    [TestMethod]
    public async Task ACancelledSwitchKeepsRunsOutUntilItsDisposed()
    {
        var gate = new VpnGate();
        var run = gate.TryEnterRun()!;
        var ticket = gate.QueueSwitch();
        using var cancellation = new CancellationTokenSource();

        var waiting = ticket.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => waiting);
        run.Dispose();
        Assert.IsNull(gate.TryEnterRun());
        ticket.Dispose();

        using var after = gate.TryEnterRun();
        Assert.IsNotNull(after);
    }

    [TestMethod]
    public void AGivenUpSwitchLetsTheNextOneHaveTheGate()
    {
        var gate = new VpnGate();
        var run = gate.TryEnterRun()!;
        var first = gate.QueueSwitch();
        using var second = gate.QueueSwitch();
        var firstWaiting = first.WaitAsync(TestContext.CancellationToken);
        var secondWaiting = second.WaitAsync(TestContext.CancellationToken);

        first.Dispose();
        run.Dispose();

        Assert.IsTrue(firstWaiting.IsCanceled);
        Assert.IsTrue(secondWaiting.IsCompletedSuccessfully);
    }

    [TestMethod]
    public void LettingGoTwiceCountsOnce()
    {
        var gate = new VpnGate();
        var run = gate.TryEnterRun()!;
        using var other = gate.TryEnterRun();
        var ticket = gate.QueueSwitch();

        run.Dispose();
        run.Dispose();
        ticket.Dispose();
        ticket.Dispose();

        Assert.AreEqual(1, gate.Runs);
        Assert.IsFalse(gate.HoldsUpRuns);
    }

    [TestMethod]
    public void RunsMayEnterOnceTheLastSwitchIsDone()
    {
        var gate = new VpnGate();
        var raised = 0;
        gate.RunsMayEnter += () => raised++;
        var first = gate.QueueSwitch();
        var second = gate.QueueSwitch();

        first.Dispose();
        Assert.AreEqual(0, raised);
        second.Dispose();
        Assert.AreEqual(1, raised);

        second.Dispose();
        Assert.AreEqual(1, raised);
    }

    [TestMethod]
    public async Task RunsMayEnterWhenASwitchHoldingTheGateIsDone()
    {
        var gate = new VpnGate();
        var raised = 0;
        gate.RunsMayEnter += () => raised++;
        var ticket = gate.QueueSwitch();
        await ticket.WaitAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, raised);
        ticket.Dispose();

        Assert.AreEqual(1, raised);
        using var run = gate.TryEnterRun();
        Assert.IsNotNull(run);
    }
}
