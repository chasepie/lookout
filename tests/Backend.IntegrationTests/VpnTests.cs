using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// The VPN's rules, at <see cref="Vpn"/>'s interface: how it switches region, how runs and switches wait for each
/// other, and how failovers go, against a fake gluetun whose tunnel comes up at once, on a fake clock. What the run
/// worker does with the VPN's answers is in <see cref="VpnFailoverTests"/>.
/// </summary>
[TestClass]
public sealed class VpnTests
{
    private const string Albania = "Albania";
    private const string Belgium = "Belgium";
    private const string Chile = "Chile";
    private const string Denmark = "Denmark";
    private const string Berlin = "DE Berlin";

    // Set before the Backend starts, and replaced by the outcome of the switch it makes, which tells the tests it's done
    private const string StaleError = "An error from before the restart.";

    private static readonly DateTime LastSwitchTime = VpnHost.Start.AddDays(-1);
    private static readonly DateTime NextRotationTime = VpnHost.Start.AddDays(1);

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task TheBackendMakesItsFirstSwitchSoTheRegionIsKnown()
    {
        // gluetun picked a region of its own, and doesn't say which, so neither runs nor failovers could know it
        await using var host = await StartWithStoredRegionAsync(null, gluetunRegions: null);

        var settings = await WaitForStartupSwitchAsync(host);

        Assert.IsNull(settings.LastError);
        Assert.AreEqual(Berlin, settings.CurrentRegion);
        Assert.AreSequenceEqual(new[] { Berlin }, host.Gluetun.SwitchedTo.ToArray());
    }

    [TestMethod]
    public async Task ReplacesARegionTheServerRegionsNoLongerAllowEvenIfEveryAllowedOneIsExcluded()
    {
        // Exclusions only apply to rotation; honouring them here would leave the VPN on Iceland for good
        await using var host = await StartWithStoredRegionAsync(
            "Iceland", gluetunRegions: ["Iceland"], excludedRegions: [Berlin]);

        var settings = await WaitForStartupSwitchAsync(host);

        Assert.IsNull(settings.LastError);
        Assert.AreEqual(Berlin, settings.CurrentRegion);
        Assert.AreSequenceEqual(new[] { Berlin }, host.Gluetun.SwitchedTo.ToArray());
    }

    [TestMethod]
    public async Task ReplacingARegionCountsAsASwitch()
    {
        // The VPN changed region, so rotating right after would restart the tunnel twice in a row
        await using var host = await StartWithStoredRegionAsync("Iceland", gluetunRegions: ["Iceland"]);

        var settings = await WaitForStartupSwitchAsync(host);

        Assert.IsNull(settings.LastError);
        Assert.IsGreaterThan(LastSwitchTime, settings.LastSwitchTime!.Value);
        Assert.IsLessThan(NextRotationTime, settings.NextRotationTime!.Value);
    }

    [TestMethod]
    public async Task ReapplyingALostRegionKeepsTheRotationSchedule()
    {
        // gluetun restarted and forgot the region: it may pick a server in any region
        await using var host = await StartWithStoredRegionAsync(Berlin, gluetunRegions: null);

        var settings = await WaitForStartupSwitchAsync(host);

        Assert.IsNull(settings.LastError);
        Assert.AreEqual(Berlin, settings.CurrentRegion);
        Assert.AreSequenceEqual(new[] { Berlin }, host.Gluetun.SwitchedTo.ToArray());
        Assert.AreEqual(LastSwitchTime, settings.LastSwitchTime);
        Assert.AreEqual(NextRotationTime, settings.NextRotationTime);
    }

    [TestMethod]
    public async Task AFirstSwitchWithEveryRegionExcludedSaysSo()
    {
        // Rotation may be off, so the message mustn't speak of rotating
        await using var host = await StartWithStoredRegionAsync(null, gluetunRegions: null, excludedRegions: [Berlin]);

        var settings = await WaitForStartupSwitchAsync(host);

        Assert.AreEqual("There are no regions to switch to: every region is excluded.", settings.LastError);
        Assert.IsEmpty(host.Gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task RotationWithOnlyOneAllowedRegionBlamesTheServerRegions()
    {
        // Nothing is excluded, so blaming the exclusions would send the Owner to the wrong setting
        await using var host = await StartWithStoredRegionAsync(Berlin, gluetunRegions: [Berlin], rotationDue: true);

        var settings = await WaitForStartupSwitchAsync(host);

        Assert.Contains(EnvVars.GluetunServerRegions, settings.LastError!);
        Assert.DoesNotContain("excluded", settings.LastError!);
        Assert.IsEmpty(host.Gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task SwitchingWhenTheServerRegionsMatchNoRegionBlamesThem()
    {
        // A typo, or a region the provider dropped: the servers list itself is fine
        await using var host = await StartWithStoredRegionAsync(
            null, gluetunRegions: null, serverRegions: "US Texass", rotationDue: true);

        var settings = await WaitForStartupSwitchAsync(host);

        Assert.Contains(EnvVars.GluetunServerRegions, settings.LastError!);
        Assert.IsEmpty(host.Gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task AWaitingSwitchKeepsNewVpnRunsOutAndWaitsForTheOnesInProgress()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        var running = await host.StartRunAsync(Ct);
        var runsMayStart = 0;
        host.Vpn.RunsMayStart += () => Interlocked.Increment(ref runsMayStart);

        await host.Vpn.RequestSwitchAsync(null, Ct);
        await host.AdvanceUntilAsync(() => host.Vpn.Progress is { WaitingForRun: true }, Ct);

        Assert.IsTrue(host.Vpn.HoldsUpRuns);
        Assert.IsNull(await host.Vpn.TryStartRunAsync(Ct));
        await host.AdvanceAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.IsEmpty(host.Gluetun.SwitchedTo, "The switch waits for the run in progress.");
        Assert.AreEqual(Albania, running.Region);

        running.Dispose();
        using var later = await host.StartRunAsync(Ct);
        Assert.AreEqual(Belgium, later.Region);
        Assert.AreEqual(1, runsMayStart);
    }

    [TestMethod]
    public async Task ARunThatWaitedForASwitchIsInTheRegionItSwitchedTo()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        host.Gluetun.HoldSwitches = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.Vpn.RequestSwitchAsync(null, Ct);
        await host.AdvanceUntilAsync(() => host.Gluetun.SwitchStarted.Task.IsCompleted, Ct);

        Assert.IsNull(await host.Vpn.TryStartRunAsync(Ct));
        host.Gluetun.HoldSwitches.SetResult();
        using var run = await host.StartRunAsync(Ct);

        Assert.AreEqual(Belgium, run.Region);
    }

    [TestMethod]
    public async Task AFailoverKeepsNewVpnRunsOutWhileRunsInProgressFinishAndASecondOneOnlyFindsItLeft()
    {
        await using var host = await StartAsync([Albania, Belgium, Chile], current: Albania);
        var first = await host.StartRunAsync(Ct);
        var second = await host.StartRunAsync(Ct);

        // One of them ends in a known failure that asks for a failover, while the other is still running there
        using var firstFailover = first.QueueFailover()!;
        first.Dispose();
        Assert.IsNull(await host.Vpn.TryStartRunAsync(Ct));
        var firstLeft = firstFailover.FailOverAsync([], Ct);
        await host.AdvanceAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.IsFalse(firstLeft.IsCompleted);
        Assert.IsEmpty(host.Gluetun.SwitchedTo, "The failover waits for the run still in the region.");

        // The other finishes rather than being cut off, and asks for a failover too
        using var secondFailover = second.QueueFailover()!;
        second.Dispose();
        await host.AdvanceUntilAsync(() => firstLeft.IsCompleted, Ct);
        Assert.IsTrue(await firstLeft);
        Assert.IsTrue(await host.FailOverAsync(secondFailover, [], Ct));

        Assert.HasCount(1, host.Gluetun.SwitchedTo, "The second failover found the VPN had already left.");
        using var later = await host.StartRunAsync(Ct);
        Assert.AreEqual(host.Gluetun.SwitchedTo.Single(), later.Region);
    }

    [TestMethod]
    public async Task AFailoverFromARegionTheVpnAlreadyLeftFindsItLeftWithoutSwitching()
    {
        await using var host = await StartAsync([Albania, Belgium, Chile], current: Albania);
        var run = await host.StartRunAsync(Ct);
        using var failover = run.QueueFailover()!;
        run.Dispose();

        // Another switch gets in between the run and its failover
        await host.Vpn.RequestSwitchAsync(Chile, Ct);
        await host.AdvanceUntilAsync(async () => (await host.GetSettingsAsync(Ct)).CurrentRegion == Chile, Ct);

        Assert.IsTrue(await host.FailOverAsync(failover, [], Ct));
        Assert.AreSequenceEqual(new[] { Chile }, host.Gluetun.SwitchedTo.ToArray());
    }

    [TestMethod]
    public async Task ARotationWaitingForTheFailingRunGoesFirstAndTheFailoverOnlyFindsItLeft()
    {
        await using var host = await StartAsync([Albania, Belgium, Chile], current: Albania, settings =>
        {
            settings.RotationEnabled = true;
            settings.NextRotationTime = VpnHost.Start.AddMinutes(5);
        });
        var run = await host.StartRunAsync(Ct);
        await host.AdvanceUntilAsync(() => host.Vpn.Progress is { WaitingForRun: true }, Ct);

        using var failover = run.QueueFailover()!;
        run.Dispose();

        Assert.IsTrue(await host.FailOverAsync(failover, [], Ct));
        Assert.HasCount(1, host.Gluetun.SwitchedTo);
        var settings = await host.GetSettingsAsync(Ct);
        Assert.AreEqual(host.Gluetun.SwitchedTo.Single(), settings.CurrentRegion);
        Assert.IsNull(settings.LastError);
    }

    [TestMethod]
    public async Task AFailoverFromARegionThatWasntKnownStillSwitches()
    {
        // gluetun isn't ready at first, so the run starts before the Backend's first switch, in a region of gluetun's
        var gluetun = new FakeGluetun(null) { RefuseSwitches = true };
        await using var host = await VpnHost.StartAsync(gluetun, [Albania, Belgium], _ => { }, Ct);
        await host.WaitUntilAsync(async () => (await host.GetSettingsAsync(Ct)).LastError is not null, Ct);
        var run = await host.StartRunAsync(Ct);
        Assert.IsNull(run.Region);
        using var failover = run.QueueFailover()!;
        run.Dispose();

        // By the time the failover runs, the first switch may have landed in that same region
        gluetun.RefuseSwitches = false;
        await host.AdvanceUntilAsync(() => !gluetun.SwitchedTo.IsEmpty, Ct);
        var firstSwitch = gluetun.SwitchedTo.Single();

        Assert.IsTrue(await host.FailOverAsync(failover, [], Ct));
        Assert.HasCount(2, gluetun.SwitchedTo);
        Assert.AreNotEqual(firstSwitch, gluetun.SwitchedTo.Last());
    }

    [TestMethod]
    public async Task AFailoverAvoidsExcludedRegionsAndTheRegionsTheWatchFailedIn()
    {
        await using var host = await StartAsync(
            [Albania, Belgium, Chile, Denmark], current: Albania, settings => settings.ExcludedRegions = [Denmark]);

        var first = await host.StartRunAsync(Ct);
        using var firstFailover = first.QueueFailover()!;
        first.Dispose();
        Assert.IsTrue(await host.FailOverAsync(firstFailover, [Belgium], Ct));
        Assert.AreSequenceEqual(new[] { Chile }, host.Gluetun.SwitchedTo.ToArray());

        // Running out of regions ends the watch's streak, and isn't something wrong with the VPN
        var second = await host.StartRunAsync(Ct);
        Assert.AreEqual(Chile, second.Region);
        using var secondFailover = second.QueueFailover()!;
        second.Dispose();
        Assert.IsFalse(await host.FailOverAsync(secondFailover, [Albania, Belgium], Ct));
        Assert.HasCount(1, host.Gluetun.SwitchedTo);
        Assert.IsNull((await host.GetSettingsAsync(Ct)).LastError);
    }

    [TestMethod]
    public async Task AFailoverWithNoRegionAllowedSaysWhy()
    {
        // Not the streak's doing: rotation couldn't leave either
        await using var host = await StartAsync(
            [Albania, Belgium], current: Albania, settings => settings.ExcludedRegions = [Belgium]);

        Assert.IsFalse(await FailOverFromANewRunAsync(host));

        Assert.AreEqual(
            "There are no regions to fail over to: every region other than the current one is excluded.",
            (await host.GetSettingsAsync(Ct)).LastError);
        Assert.IsEmpty(host.Gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task AFailedFailoverIsRecordedAsTheVpnsError()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        host.Gluetun.RefuseSwitches = true;

        Assert.IsFalse(await FailOverFromANewRunAsync(host));

        var settings = await host.GetSettingsAsync(Ct);
        Assert.Contains("gluetun is unwell", settings.LastError!);
        Assert.AreEqual(Albania, settings.CurrentRegion);
    }

    [TestMethod]
    public async Task ASuccessfulFailoverRestartsTheRotationTimer()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania, settings =>
        {
            settings.RotationEnabled = true;
            settings.RotationIntervalMinutes = 15;
            settings.RotationJitterMinutes = 0;
            settings.NextRotationTime = VpnHost.Start.AddDays(1);
        });

        Assert.IsTrue(await FailOverFromANewRunAsync(host));

        var settings = await host.GetSettingsAsync(Ct);
        Assert.IsGreaterThanOrEqualTo(VpnHost.Start, settings.LastSwitchTime!.Value);
        Assert.AreEqual(settings.LastSwitchTime.Value.AddMinutes(15), settings.NextRotationTime);
    }

    [TestMethod]
    public async Task AManualSwitchGoesToTheListsSpellingOfTheRegionWhateverTheCaseTyped()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);

        Assert.AreEqual(ManualSwitchResult.Queued, await host.Vpn.RequestSwitchAsync(" belgium ", Ct));
        await host.AdvanceUntilAsync(() => !host.Gluetun.SwitchedTo.IsEmpty, Ct);

        Assert.AreSequenceEqual(new[] { Belgium }, host.Gluetun.SwitchedTo.ToArray());
    }

    [TestMethod]
    [DataRow("Atlantis", DisplayName = "Not the provider's")]
    [DataRow(Chile, DisplayName = "Not in the server regions")]
    public async Task AManualSwitchToARegionNotInTheListIsRefused(string region)
    {
        var gluetun = new FakeGluetun([Albania]);
        await using var host = await VpnHost.StartAsync(
            gluetun, [Albania, Belgium, Chile], s => s.CurrentRegion = Albania, Ct, serverRegions: $"{Albania},{Belgium}");

        Assert.AreEqual(ManualSwitchResult.UnknownRegion, await host.Vpn.RequestSwitchAsync(region, Ct));
        await host.AdvanceAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.IsEmpty(gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task AManualSwitchToARegionCantBeCheckedWhileGluetunIsUnreachable()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        host.Gluetun.Unreachable = true;

        await Assert.ThrowsExactlyAsync<GluetunException>(() => host.Vpn.RequestSwitchAsync(Belgium, Ct));
    }

    [TestMethod]
    public async Task ATunnelThatDoesntComeUpInTimeIsRecordedAsTheVpnsError()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        host.Gluetun.TunnelDown = true;
        var requested = host.Now;

        await host.Vpn.RequestSwitchAsync(Belgium, Ct);
        VpnSettings settings = null!;
        await host.AdvanceUntilAsync(async () => (settings = await host.GetSettingsAsync(Ct)).LastError is not null, Ct);

        Assert.AreEqual("Switched to Belgium, but the tunnel wasn't back up after 2 minutes.", settings.LastError);
        Assert.AreEqual(Belgium, settings.CurrentRegion);
        Assert.IsGreaterThanOrEqualTo(requested.AddMinutes(2), host.Now);
    }

    [TestMethod]
    public async Task AFailedRotationIsTriedAgainAfterTheRetryDelayAndNotBefore()
    {
        var gluetun = new FakeGluetun([Albania]) { RefuseSwitches = true };
        await using var host = await VpnHost.StartAsync(gluetun, [Albania, Belgium], settings =>
        {
            settings.CurrentRegion = Albania;
            settings.RotationEnabled = true;
            settings.NextRotationTime = VpnHost.Start.AddMinutes(-1);
        }, Ct);
        await host.WaitUntilAsync(async () => (await host.GetSettingsAsync(Ct)).LastError is not null, Ct);
        Assert.AreEqual(VpnHost.Start.AddMinutes(1), (await host.GetSettingsAsync(Ct)).NextRotationTime);
        gluetun.RefuseSwitches = false;

        await host.AdvanceAsync(TimeSpan.FromSeconds(59), Ct);
        Assert.IsEmpty(gluetun.SwitchedTo);

        await host.AdvanceUntilAsync(async () => (await host.GetSettingsAsync(Ct)).LastError is null, Ct);
        Assert.AreSequenceEqual(new[] { Belgium }, gluetun.SwitchedTo.ToArray());
    }

    [TestMethod]
    public async Task ARegionGluetunLostIsReappliedAtTheNextReconcileAndNotBefore()
    {
        // The Backend checked gluetun's region as it started
        await using var host = await StartAsync([Albania, Belgium], current: Albania);

        host.Gluetun.ForgetRegion();
        await host.AdvanceAsync(TimeSpan.FromSeconds(59), Ct);
        Assert.IsEmpty(host.Gluetun.SwitchedTo);

        await host.AdvanceUntilAsync(() => !host.Gluetun.SwitchedTo.IsEmpty, Ct);
        Assert.AreSequenceEqual(new[] { Albania }, host.Gluetun.SwitchedTo.ToArray());
        Assert.IsGreaterThanOrEqualTo(VpnHost.Start.AddMinutes(1), host.Now);
    }

    [TestMethod]
    public async Task ACheckOfGluetunsRegionDoesntUndoAFailoverThatLandedWhileItAsked()
    {
        // The check read the VPN's region, Albania, then asks gluetun for its own, which answers once the failover is done
        var gluetunAnswers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gluetun = new FakeGluetun([Albania]) { HoldNextSettingsRead = gluetunAnswers };
        await using var host = await VpnHost.StartAsync(gluetun, [Albania, Belgium], s => s.CurrentRegion = Albania, Ct);

        Assert.IsTrue(await FailOverFromANewRunAsync(host));
        gluetunAnswers.SetResult();
        await host.AdvanceAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.AreSequenceEqual(new[] { Belgium }, gluetun.SwitchedTo.ToArray());
        Assert.AreEqual(Belgium, (await host.GetSettingsAsync(Ct)).CurrentRegion);
    }

    [TestMethod]
    public async Task AReplacementTheCheckAskedForIsDroppedIfAFailoverLeftTheRegionFirst()
    {
        // Iceland is no longer allowed, but gluetun refuses the first replacement
        var gluetun = new FakeGluetun(["Iceland"]) { RefuseSwitches = true };
        await using var host = await VpnHost.StartAsync(
            gluetun, [Albania, Belgium, "Iceland"], s => s.CurrentRegion = "Iceland", Ct,
            serverRegions: $"{Albania},{Belgium}");
        await host.WaitUntilAsync(async () => (await host.GetSettingsAsync(Ct)).LastError is not null, Ct);
        gluetun.RefuseSwitches = false;

        // A failover from Iceland is switching when the next check finds the VPN still there, and asks to replace it
        var gluetunSwitches = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gluetun.HoldSwitches = gluetunSwitches;
        var run = await host.StartRunAsync(Ct);
        using var failover = run.QueueFailover()!;
        run.Dispose();
        var failingOver = failover.FailOverAsync([], Ct);
        await host.AdvanceAsync(TimeSpan.FromSeconds(61), Ct);
        gluetunSwitches.SetResult();

        await host.AdvanceUntilAsync(() => failingOver.IsCompleted, Ct);
        Assert.IsTrue(await failingOver);
        await host.AdvanceAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.HasCount(1, gluetun.SwitchedTo);
        Assert.AreEqual(gluetun.SwitchedTo.Single(), (await host.GetSettingsAsync(Ct)).CurrentRegion);
    }

    [TestMethod]
    public async Task AFirstSwitchTheCheckAskedForIsDroppedIfAFailoverSwitchedFirst()
    {
        // The Backend hasn't switched yet, and gluetun refuses its first switch
        var gluetun = new FakeGluetun(null) { RefuseSwitches = true };
        await using var host = await VpnHost.StartAsync(gluetun, [Albania, Belgium], _ => { }, Ct);
        await host.WaitUntilAsync(async () => (await host.GetSettingsAsync(Ct)).LastError is not null, Ct);
        gluetun.RefuseSwitches = false;

        // A failover is switching when the next check finds no region yet, and asks for a first switch
        var gluetunSwitches = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gluetun.HoldSwitches = gluetunSwitches;
        var run = await host.StartRunAsync(Ct);
        using var failover = run.QueueFailover()!;
        run.Dispose();
        var failingOver = failover.FailOverAsync([], Ct);
        await host.AdvanceAsync(TimeSpan.FromSeconds(61), Ct);
        gluetunSwitches.SetResult();

        await host.AdvanceUntilAsync(() => failingOver.IsCompleted, Ct);
        Assert.IsTrue(await failingOver);
        await host.AdvanceAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.HasCount(1, gluetun.SwitchedTo);
        Assert.AreEqual(gluetun.SwitchedTo.Single(), (await host.GetSettingsAsync(Ct)).CurrentRegion);
    }

    /// <summary>
    /// Starts the Backend with gluetun offering <paramref name="regions"/>, and the VPN in <paramref name="current"/>,
    /// where gluetun is too.
    /// </summary>
    private Task<VpnHost> StartAsync(string[] regions, string current, Action<VpnSettings>? configure = null) =>
        VpnHost.StartAsync(new FakeGluetun([current]), regions, settings =>
        {
            settings.CurrentRegion = current;
            configure?.Invoke(settings);
        }, Ct);

    /// <summary>
    /// Starts a VPN run whose known failure asks for a failover, and runs the failover.
    /// </summary>
    /// <returns>Whether the VPN left the run's region.</returns>
    private async Task<bool> FailOverFromANewRunAsync(VpnHost host)
    {
        var run = await host.StartRunAsync(Ct);
        using var failover = run.QueueFailover()!;
        run.Dispose();
        return await host.FailOverAsync(failover, [], Ct);
    }

    /// <summary>
    /// Stores <paramref name="currentRegion"/> as a Backend without the VPN would have, with rotation on and an error
    /// from before, then starts one limited to <paramref name="serverRegions"/>.
    /// </summary>
    /// <param name="gluetunRegions">The regions gluetun is restricted to when the Backend starts.</param>
    /// <param name="rotationDue">Whether a rotation is due when the Backend starts, rather than in a day.</param>
    private Task<VpnHost> StartWithStoredRegionAsync(
        string? currentRegion,
        string[]? gluetunRegions,
        string[]? excludedRegions = null,
        string serverRegions = Berlin,
        bool rotationDue = false) =>
        VpnHost.StartAsync(
            new FakeGluetun(gluetunRegions),
            [Berlin, "Iceland", "US Texas"],
            settings =>
            {
                settings.RotationEnabled = true;
                settings.ExcludedRegions = [.. excludedRegions ?? []];
                settings.CurrentRegion = currentRegion;
                settings.LastSwitchTime = LastSwitchTime;
                settings.NextRotationTime = rotationDue ? LastSwitchTime : NextRotationTime;
                settings.LastError = StaleError;
            },
            Ct,
            serverRegions);

    /// <summary>
    /// The settings once the switch the Backend makes when it starts has recorded its outcome.
    /// </summary>
    private async Task<VpnSettings> WaitForStartupSwitchAsync(VpnHost host)
    {
        VpnSettings settings = null!;
        await host.AdvanceUntilAsync(
            async () => (settings = await host.GetSettingsAsync(Ct)).LastError != StaleError, Ct);
        return settings;
    }
}
