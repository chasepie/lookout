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

        var entering = gate.EnterSwitchAsync(TestContext.CancellationToken);
        first.Dispose();
        Assert.IsFalse(entering.IsCompleted);
        second.Dispose();

        Assert.IsTrue(entering.IsCompletedSuccessfully);
        entering.Result.Dispose();
    }

    [TestMethod]
    public async Task AWaitingSwitchKeepsNewRunsOutUntilItsDone()
    {
        var gate = new VpnGate();
        var run = gate.TryEnterRun()!;
        var entering = gate.EnterSwitchAsync(TestContext.CancellationToken);

        Assert.IsTrue(gate.HoldsUpRuns);
        Assert.IsNull(gate.TryEnterRun());
        run.Dispose();
        var hold = await entering;
        Assert.IsNull(gate.TryEnterRun());
        hold.Dispose();

        Assert.IsFalse(gate.HoldsUpRuns);
        using var after = gate.TryEnterRun();
        Assert.IsNotNull(after);
    }

    [TestMethod]
    public async Task OnlyOneSwitchHasTheGateAtATime()
    {
        var gate = new VpnGate();
        var hold = await gate.EnterSwitchAsync(TestContext.CancellationToken);

        Assert.ThrowsExactly<InvalidOperationException>(() => gate.EnterSwitchAsync(TestContext.CancellationToken));
        hold.Dispose();

        using var next = await gate.EnterSwitchAsync(TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task ACancelledSwitchLetsRunsInAgain()
    {
        var gate = new VpnGate();
        var raised = 0;
        gate.RunsMayEnter += () => raised++;
        using var run = gate.TryEnterRun()!;
        using var cancellation = new CancellationTokenSource();

        var entering = gate.EnterSwitchAsync(cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => entering);
        Assert.AreEqual(1, raised);
        using var after = gate.TryEnterRun();
        Assert.IsNotNull(after);
    }

    [TestMethod]
    public async Task CancellingASwitchThatHasTheGateDoesNothing()
    {
        var gate = new VpnGate();
        using var cancellation = new CancellationTokenSource();
        var hold = await gate.EnterSwitchAsync(cancellation.Token);

        await cancellation.CancelAsync();

        Assert.IsTrue(gate.HoldsUpRuns);
        hold.Dispose();
        Assert.IsFalse(gate.HoldsUpRuns);
    }

    [TestMethod]
    public async Task LettingGoTwiceCountsOnce()
    {
        var gate = new VpnGate();
        var raised = 0;
        gate.RunsMayEnter += () => raised++;
        var run = gate.TryEnterRun()!;
        using var other = gate.TryEnterRun();

        run.Dispose();
        run.Dispose();
        Assert.AreEqual(1, gate.Runs);
        other!.Dispose();
        var hold = await gate.EnterSwitchAsync(TestContext.CancellationToken);
        hold.Dispose();
        hold.Dispose();

        Assert.AreEqual(1, raised);
        Assert.IsFalse(gate.HoldsUpRuns);
    }

    [TestMethod]
    public async Task RunsMayEnterWhenASwitchHoldingTheGateIsDone()
    {
        var gate = new VpnGate();
        var raised = 0;
        gate.RunsMayEnter += () => raised++;
        var hold = await gate.EnterSwitchAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, raised);
        hold.Dispose();

        Assert.AreEqual(1, raised);
        using var run = gate.TryEnterRun();
        Assert.IsNotNull(run);
    }
}
