using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// The VPN's rules, against a fake gluetun whose tunnel comes up at once, on a fake clock: how it switches region, and
/// how the run worker, which schedules both runs and switches, fits switches around VPN runs. What the run worker does
/// with avoided regions after a run asks for one is in <see cref="AvoidedRegionsTests"/>.
/// </summary>
[TestClass]
public sealed class VpnTests
{
    private const string Albania = "Albania";
    private const string Belgium = "Belgium";
    private const string Chile = "Chile";
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
        // gluetun picked a region of its own, and doesn't say which, so runs couldn't know it
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
    public async Task APlannedSwitchWaitsForTheVpnRunsInProgressAndKeepsNewOnesOut()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        var hold = HoldFirstRun(host);
        var runningId = await host.AddVpnWatchAsync(Ct);
        var laterId = await host.AddVpnWatchAsync(Ct);
        await host.Client.QueueRunAsync(runningId, Ct);
        await host.WaitUntilAsync(() => host.Runner.InProgress == 1, Ct);

        await host.Vpn.RequestSwitchAsync(null, Ct);
        await host.WaitUntilAsync(() => host.Vpn.Progress is { WaitingForRun: true }, Ct);
        await host.Client.QueueRunAsync(laterId, Ct);
        await host.AdvanceAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.IsEmpty(host.Gluetun.SwitchedTo, "The switch waits for the run in progress.");
        Assert.AreEqual(WatchRunStatus.Created, (await host.GetRunAsync(laterId, Ct)).Status);
        hold.SetResult();
        Assert.AreEqual(Albania, (await host.WaitForRunAsync(runningId, 0, Ct)).Region);
        Assert.AreEqual(Belgium, (await host.WaitForRunAsync(laterId, 0, Ct)).Region);
        Assert.AreSequenceEqual(new[] { Belgium }, host.Gluetun.SwitchedTo.ToArray());
    }

    [TestMethod]
    public async Task RunsWithoutTheVpnKeepStartingWhileAPlannedSwitchWaits()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        var hold = HoldFirstRun(host);
        var vpnId = await host.AddVpnWatchAsync(Ct);
        var plainId = await host.AddWatchWithoutVpnAsync(Ct);
        await host.Client.QueueRunAsync(vpnId, Ct);
        await host.WaitUntilAsync(() => host.Runner.InProgress == 1, Ct);
        await host.Vpn.RequestSwitchAsync(null, Ct);
        await host.WaitUntilAsync(() => host.Vpn.Progress is { WaitingForRun: true }, Ct);

        var plain = await host.RunAsync(plainId, Ct);

        Assert.IsNull(plain.Region);
        Assert.IsNull(plain.Error);
        Assert.IsTrue(host.Vpn.Progress?.WaitingForRun, "The switch still waits for the VPN run.");
        hold.SetResult();
        await host.AdvanceUntilAsync(() => !host.Gluetun.SwitchedTo.IsEmpty, Ct);
    }

    [TestMethod]
    public async Task ARunThatWaitedForASwitchIsInTheRegionItSwitchedTo()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        host.Gluetun.HoldSwitches = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchId = await host.AddVpnWatchAsync(Ct);
        await host.Vpn.RequestSwitchAsync(null, Ct);
        await host.WaitUntilAsync(() => host.Gluetun.SwitchStarted.Task.IsCompleted, Ct);

        await host.Client.QueueRunAsync(watchId, Ct);
        await host.AdvanceAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.AreEqual(WatchRunStatus.Created, (await host.GetRunAsync(watchId, Ct)).Status);
        host.Gluetun.HoldSwitches.SetResult();

        Assert.AreEqual(Belgium, (await host.WaitForRunAsync(watchId, 0, Ct)).Region);
    }

    [TestMethod]
    public async Task VpnRunsWaitForTheBackendsFirstSwitch()
    {
        // gluetun isn't ready at first, so the Backend can't make its first switch, and gluetun's region isn't known
        var gluetun = new FakeGluetun(null) { RefuseSwitches = true };
        await using var host = await VpnHost.StartAsync(gluetun, [Albania, Belgium], _ => { }, Ct);
        await host.WaitUntilAsync(async () => (await host.GetSettingsAsync(Ct)).LastError is not null, Ct);
        var watchId = await host.AddVpnWatchAsync(Ct);

        await host.Client.QueueRunAsync(watchId, Ct);
        await host.AdvanceAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.AreEqual(WatchRunStatus.Created, (await host.GetRunAsync(watchId, Ct)).Status);

        gluetun.RefuseSwitches = false;
        var run = await host.WaitForRunAsync(watchId, 0, Ct);
        Assert.AreEqual(gluetun.SwitchedTo.Single(), run.Region);
    }

    [TestMethod]
    public async Task ARunInARegionItsWatchAvoidsWaitsWhileTheVpnSwitchesToOneItDoesnt()
    {
        await using var host = await StartAsync([Albania, Belgium, Chile], current: Albania);
        var watchId = await host.AddVpnWatchAsync(Ct, Albania, Belgium);

        var run = await host.RunAsync(watchId, Ct);

        Assert.AreEqual(Chile, run.Region);
        Assert.AreSequenceEqual(new[] { Chile }, host.Gluetun.SwitchedTo.ToArray());
    }

    [TestMethod]
    public async Task ARunOfAWatchThatAvoidsEveryRegionTheOwnerHasntExcludedCrashesWithoutHoldingUpOthers()
    {
        await using var host = await StartAsync(
            [Albania, Belgium, Chile], current: Albania, settings => settings.ExcludedRegions = [Chile]);
        var watchId = await host.AddVpnWatchAsync(Ct, Albania, Belgium);
        var otherId = await host.AddVpnWatchAsync(Ct);

        var run = await host.RunAsync(watchId, Ct);

        Assert.AreEqual(WatchRunWorker.NoRegionLeftError, run.Error);
        Assert.IsNull(run.Region);
        Assert.IsEmpty(host.Runner.Requests);
        Assert.IsNull(host.Vpn.Progress, "No switch is planned for it.");
        Assert.AreEqual(Albania, (await host.RunAsync(otherId, Ct)).Region);
        Assert.IsEmpty(host.Gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task APlannedRotationIsDroppedIfTheOwnerTurnsRotationOffWhileItWaits()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania, settings =>
        {
            settings.RotationEnabled = true;
            // Due once the run is in progress, since the clock only moves when the test moves it
            settings.NextRotationTime = VpnHost.Start.AddSeconds(5);
        });
        var hold = HoldFirstRun(host);
        var watchId = await host.AddVpnWatchAsync(Ct);
        await host.Client.QueueRunAsync(watchId, Ct);
        await host.WaitUntilAsync(() => host.Runner.InProgress == 1, Ct);
        await host.AdvanceUntilAsync(() => host.Vpn.Progress is { WaitingForRun: true }, Ct);

        var settings = await host.GetSettingsAsync(Ct);
        await host.Vpn.UpdateSettingsAsync(new VpnSettingsDto
        {
            RotationEnabled = false,
            RotationIntervalMinutes = settings.RotationIntervalMinutes,
            RotationJitterMinutes = settings.RotationJitterMinutes,
        }, Ct);
        hold.SetResult();
        await host.WaitForRunAsync(watchId, 0, Ct);
        await host.AdvanceUntilAsync(() => host.Vpn.Progress is null, Ct);

        Assert.IsEmpty(host.Gluetun.SwitchedTo);
        Assert.AreEqual(Albania, (await host.RunAsync(watchId, Ct)).Region);
    }

    [TestMethod]
    public async Task APlannedSwitchForAWatchIsDroppedIfTheOwnerClearsTheRegionWhileItWaits()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        var hold = HoldFirstRun(host);
        var otherId = await host.AddVpnWatchAsync(Ct);
        var avoidingId = await host.AddVpnWatchAsync(Ct, Albania);
        await host.Client.QueueRunAsync(otherId, Ct);
        await host.WaitUntilAsync(() => host.Runner.InProgress == 1, Ct);
        await host.Client.QueueRunAsync(avoidingId, Ct);
        await host.WaitUntilAsync(() => host.Vpn.Progress is { WaitingForRun: true }, Ct);

        await host.ClearAvoidedRegionsAsync(avoidingId, Ct);
        hold.SetResult();

        Assert.AreEqual(Albania, (await host.WaitForRunAsync(avoidingId, 0, Ct)).Region);
        Assert.IsEmpty(host.Gluetun.SwitchedTo);
        Assert.IsNull(host.Vpn.Progress);
    }

    [TestMethod]
    public async Task APlannedSwitchForAWatchIsDroppedIfTheOwnerTurnsItsVpnOffWhileItWaits()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        var hold = HoldFirstRun(host);
        var otherId = await host.AddVpnWatchAsync(Ct);
        var avoidingId = await host.AddVpnWatchAsync(Ct, Albania);
        await host.Client.QueueRunAsync(otherId, Ct);
        await host.WaitUntilAsync(() => host.Runner.InProgress == 1, Ct);
        await host.Client.QueueRunAsync(avoidingId, Ct);
        await host.WaitUntilAsync(() => host.Vpn.Progress is { WaitingForRun: true }, Ct);

        await host.TurnVpnOffAsync(avoidingId, Ct);
        hold.SetResult();
        await host.WaitForRunAsync(otherId, 0, Ct);
        await host.AdvanceUntilAsync(() => host.Vpn.Progress is null, Ct);
        await host.AdvanceAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.IsEmpty(host.Gluetun.SwitchedTo);
        Assert.IsNull(host.Vpn.Progress);
    }

    [TestMethod]
    public async Task APlannedSwitchForAWatchIsDroppedIfTheOwnerDeletesItsPendingRunWhileItWaits()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        var hold = HoldFirstRun(host);
        var otherId = await host.AddVpnWatchAsync(Ct);
        var avoidingId = await host.AddVpnWatchAsync(Ct, Albania);
        await host.Client.QueueRunAsync(otherId, Ct);
        await host.WaitUntilAsync(() => host.Runner.InProgress == 1, Ct);
        await host.Client.QueueRunAsync(avoidingId, Ct);
        await host.WaitUntilAsync(() => host.Vpn.Progress is { WaitingForRun: true }, Ct);

        using (var response = await host.Client.DeleteAsync($"/api/Watches/{avoidingId}/runs", Ct))
        {
            response.EnsureSuccessStatusCode();
        }

        hold.SetResult();
        await host.WaitForRunAsync(otherId, 0, Ct);
        await host.AdvanceUntilAsync(() => host.Vpn.Progress is null, Ct);
        await host.AdvanceAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.IsEmpty(host.Gluetun.SwitchedTo);
        Assert.IsNull(host.Vpn.Progress);
    }

    [TestMethod]
    public async Task AFailedSwitchForAWatchIsTriedAgainAfterTheRetryDelayAndNotBefore()
    {
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        host.Gluetun.RefuseSwitches = true;
        var watchId = await host.AddVpnWatchAsync(Ct, Albania);

        await host.Client.QueueRunAsync(watchId, Ct);
        await host.WaitUntilAsync(async () => (await host.GetSettingsAsync(Ct)).LastError is not null, Ct);
        var failed = host.Now;
        host.Gluetun.RefuseSwitches = false;
        await host.AdvanceAsync(TimeSpan.FromSeconds(50), Ct);
        Assert.IsEmpty(host.Gluetun.SwitchedTo);

        var run = await host.WaitForRunAsync(watchId, 0, Ct);
        Assert.AreEqual(Belgium, run.Region);
        Assert.IsGreaterThanOrEqualTo(failed.AddMinutes(1), host.Now);
    }

    [TestMethod]
    public async Task ARunInAnAvoidedRegionWaitsRatherThanCrashingWhileGluetunCantBeReached()
    {
        // Its regions can't be read, so whether the watch has a region left isn't known
        await using var host = await StartAsync([Albania, Belgium], current: Albania);
        host.Gluetun.Unreachable = true;
        var watchId = await host.AddVpnWatchAsync(Ct, Albania);

        await host.Client.QueueRunAsync(watchId, Ct);
        VpnSettings settings = null!;
        await host.WaitUntilAsync(async () => (settings = await host.GetSettingsAsync(Ct)).LastError is not null, Ct);
        var failed = host.Now;
        host.Gluetun.Unreachable = false;
        await host.AdvanceAsync(TimeSpan.FromSeconds(50), Ct);

        Assert.StartsWith("Couldn't reach gluetun", settings.LastError!);
        var waiting = await host.GetRunAsync(watchId, Ct);
        Assert.AreEqual(WatchRunStatus.Created, waiting.Status);
        Assert.IsNull(waiting.Error);
        var run = await host.WaitForRunAsync(watchId, 0, Ct);
        Assert.AreEqual(Belgium, run.Region);
        Assert.IsGreaterThanOrEqualTo(failed.AddMinutes(1), host.Now);
    }

    [TestMethod]
    public async Task RotationPrefersARegionNoWatchAvoids()
    {
        await using var host = await StartAsync([Albania, Belgium, Chile], current: Albania, settings =>
        {
            settings.RotationEnabled = true;
            // Due once the watch is added, since the clock only moves when the test moves it
            settings.NextRotationTime = VpnHost.Start.AddSeconds(5);
        });
        await host.AddVpnWatchAsync(Ct, Belgium);

        await host.AdvanceUntilAsync(() => !host.Gluetun.SwitchedTo.IsEmpty, Ct);

        Assert.AreSequenceEqual(new[] { Chile }, host.Gluetun.SwitchedTo.ToArray());
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

    /// <summary>
    /// Holds the next run the Runner gets until the test lets go, and lets later ones run at once.
    /// </summary>
    private static TaskCompletionSource HoldFirstRun(VpnHost host)
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        host.Runner.WhileRunning = () => Interlocked.Increment(ref calls) == 1 ? hold.Task : Task.CompletedTask;
        return hold;
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
