using ChasePie.Lookout.Backend.Controllers;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Failovers that known failures ask for, the retries after them, and how VPN runs and region switches wait for each
/// other, with the Runner and gluetun faked at their HTTP boundaries.
/// </summary>
[TestClass]
public sealed class VpnFailoverTests
{
    private const string Albania = "Albania";
    private const string Belgium = "Belgium";
    private const string Chile = "Chile";
    private const string Denmark = "Denmark";

    private static readonly object FailoverFailure = new { success = false, failureReason = "Blocked", failover = true };
    private static readonly object PlainFailure = new { success = false, failureReason = "Sold out" };
    private static readonly object Success = new { success = true, content = "In stock" };
    private static readonly object Crash = new { crash = new { error = "Error: No price" } };

    // Long enough for the worker to have started a run, or the rotation service a switch, were it going to
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task AKnownFailureAskingForAFailoverSwitchesTheRegionAndRetriesTheWatchThere()
    {
        // Rotation is off, which doesn't stop a failover
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        lookout.Runner.Response = Success;
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        await lookout.Client.RunAsync(watchId, Ct);
        var runs = await lookout.Client.WaitForRunsAsync(watchId, 2, Ct);

        var (failed, retry) = (runs[0], runs[1]);
        Assert.AreEqual(Albania, failed.Region);
        Assert.IsTrue(failed.AskedForFailover);
        Assert.IsNull(failed.RetryOfRunId);
        Assert.AreEqual(failed.Id, retry.RetryOfRunId);
        Assert.AreEqual(Belgium, retry.Region);
        Assert.AreEqual("In stock", retry.Content);
        Assert.AreSequenceEqual(new[] { Belgium }, lookout.Gluetun.SwitchedTo.ToArray());
        var settings = await lookout.GetVpnSettingsAsync(Ct);
        Assert.AreEqual(Belgium, settings.CurrentRegion);
        Assert.IsNull(settings.LastError);
        Assert.IsNotNull(settings.LastSwitchTime);
    }

    [TestMethod]
    [DataRow("plain known failure")]
    [DataRow("success")]
    [DataRow("crash")]
    public async Task OnlyAKnownFailureAskingForOneFailsOver(string outcome)
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Runner.Response = outcome switch
        {
            "plain known failure" => PlainFailure,
            "success" => Success,
            _ => Crash,
        };
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        await lookout.Client.RunAsync(watchId, Ct);
        var runs = await SettleAsync(lookout, watchId);

        Assert.HasCount(2, runs);
        Assert.IsFalse(runs[0].AskedForFailover);
        Assert.IsNull(runs[1].RetryOfRunId);
        Assert.IsEmpty(lookout.Gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task AWatchWithoutTheVpnOnlyRecordsItsRequest()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct);

        await lookout.Client.RunAsync(watchId, Ct);
        var runs = await SettleAsync(lookout, watchId);

        Assert.HasCount(2, runs);
        Assert.IsTrue(runs[0].AskedForFailover);
        Assert.IsNull(runs[0].Region);
        Assert.IsNull(runs[1].RetryOfRunId);
        Assert.IsEmpty(lookout.Gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task AFailoverAvoidsExcludedRegionsAndThoseTheStreakFailedInUntilASuccess()
    {
        await using var lookout = await StartAsync(
            [Albania, Belgium, Chile, Denmark], current: Albania, settings => settings.ExcludedRegions = [Denmark]);
        for (var i = 0; i < 3; i++)
        {
            lookout.Runner.Responses.Enqueue(FailoverFailure);
        }

        lookout.Runner.Response = Success;
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        // Albania fails over to Belgium or Chile, that to the other, and then no region is left
        await lookout.Client.RunAsync(watchId, Ct);
        await lookout.Client.WaitForRunsAsync(watchId, 3, Ct);
        var runs = await SettleAsync(lookout, watchId);

        Assert.HasCount(4, runs);
        Assert.AreSequenceEqual(new[] { Belgium, Chile }, lookout.Gluetun.SwitchedTo.Order().ToArray());
        Assert.AreEqual(runs[1].Id, runs[2].RetryOfRunId);
        Assert.IsNull(runs[3].RetryOfRunId, "With no region left, there's no retry.");
        var lastRegion = runs[2].Region!;

        // The success started the streak over, so Albania and the region it first failed over to can be picked again
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        await lookout.Client.RunAsync(watchId, Ct);
        runs = await lookout.Client.WaitForRunsAsync(watchId, 6, Ct);

        Assert.HasCount(3, lookout.Gluetun.SwitchedTo);
        var thirdSwitch = lookout.Gluetun.SwitchedTo.Last();
        Assert.AreNotEqual(lastRegion, thirdSwitch);
        Assert.AreNotEqual(Denmark, thirdSwitch);
        Assert.AreEqual(runs[4].Id, runs[5].RetryOfRunId);
    }

    [TestMethod]
    public async Task AThirdRetryInARowStillFailsOverButIsntRetried()
    {
        await using var lookout = await StartAsync(["A", "B", "C", "D", "E", "F"], current: "A");
        for (var i = 0; i < 4; i++)
        {
            lookout.Runner.Responses.Enqueue(FailoverFailure);
        }

        lookout.Runner.Response = Success;
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        await lookout.Client.RunAsync(watchId, Ct);
        await lookout.Client.WaitForRunsAsync(watchId, 4, Ct);
        var runs = await SettleAsync(lookout, watchId);

        Assert.HasCount(5, runs);
        Assert.AreEqual(runs[2].Id, runs[3].RetryOfRunId);
        Assert.IsNull(runs[4].RetryOfRunId);
        Assert.HasCount(4, lookout.Gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task AFailedFailoverIsRecordedAndNotRetried()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Gluetun.RefuseSwitches = true;
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        lookout.Runner.Response = Success;
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        await lookout.Client.RunAsync(watchId, Ct);
        var runs = await SettleAsync(lookout, watchId);

        Assert.HasCount(2, runs);
        Assert.IsNull(runs[1].RetryOfRunId);
        var settings = await lookout.GetVpnSettingsAsync(Ct);
        Assert.Contains("gluetun is unwell", settings.LastError!);
        Assert.AreEqual(Albania, settings.CurrentRegion);
    }

    [TestMethod]
    public async Task AFailoverFromARegionTheVpnAlreadyLeftOnlyRetries()
    {
        await using var lookout = await StartAsync([Albania, Belgium, Chile], current: Albania);
        // Another switch got in between the run and its failover
        lookout.Runner.WhileRunning = () => lookout.UpdateVpnSettingsAsync(s => s.CurrentRegion = Chile, Ct);
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        lookout.Runner.Response = Success;
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        await lookout.Client.RunAsync(watchId, Ct);
        var runs = await lookout.Client.WaitForRunsAsync(watchId, 2, Ct);

        Assert.AreEqual(runs[0].Id, runs[1].RetryOfRunId);
        Assert.AreEqual(Chile, runs[1].Region);
        Assert.IsEmpty(lookout.Gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task ARotationWaitingForTheFailingRunGoesFirstAndTheFailoverOnlyRetries()
    {
        await using var lookout = await StartAsync([Albania, Belgium, Chile], current: Albania);
        lookout.Runner.WhileRunning = async () =>
        {
            lookout.Runner.WhileRunning = null;
            await lookout.UpdateVpnSettingsAsync(s =>
            {
                s.RotationEnabled = true;
                s.NextRotationTime = DateTime.UtcNow.AddMinutes(-1);
            }, Ct);
            // Long enough for the rotation to come due and wait for the run
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        };
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        lookout.Runner.Response = Success;
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        await lookout.Client.RunAsync(watchId, Ct);
        var runs = await lookout.Client.WaitForRunsAsync(watchId, 2, Ct);

        Assert.AreEqual(runs[0].Id, runs[1].RetryOfRunId);
        Assert.HasCount(1, lookout.Gluetun.SwitchedTo);
        Assert.AreEqual(lookout.Gluetun.SwitchedTo.Single(), runs[1].Region);
        Assert.IsNull((await lookout.GetVpnSettingsAsync(Ct)).LastError);
    }

    [TestMethod]
    public async Task AVpnRunQueuedAfterAFailingOneWaitsForTheFailover()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Gluetun.HoldSwitches = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        lookout.Runner.Response = Success;
        var failingId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));
        var otherId = await lookout.Client.CreateWatchAsync("Prices", Ct, ("UseVpn", "true"));

        await lookout.Client.QueueRunAsync(failingId, Ct);
        await lookout.Gluetun.SwitchStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await lookout.Client.QueueRunAsync(otherId, Ct);
        await Task.Delay(SettleTime, Ct);
        var waiting = (await lookout.Client.GetRunsAsync(otherId, Ct)).Single();
        Assert.AreEqual(WatchRunStatus.Created, waiting.Status);

        lookout.Gluetun.HoldSwitches.SetResult();
        var other = (await lookout.Client.WaitForRunsAsync(otherId, 1, Ct)).Single();

        Assert.AreEqual(Belgium, other.Region);
    }

    [TestMethod]
    public async Task VpnRunsOfDifferentWatchesRunAtOnce()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lookout.Runner.WhileRunning = () => hold.Task;
        var firstId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));
        var secondId = await lookout.Client.CreateWatchAsync("Prices", Ct, ("UseVpn", "true"));

        await lookout.Client.QueueRunAsync(firstId, Ct);
        await lookout.Client.QueueRunAsync(secondId, Ct);
        await LookoutApi.WaitUntilAsync(() => Task.FromResult(lookout.Runner.InProgress == 2), Ct);
        hold.SetResult();

        Assert.AreEqual(Albania, (await lookout.Client.WaitForRunsAsync(firstId, 1, Ct)).Single().Region);
        Assert.AreEqual(Albania, (await lookout.Client.WaitForRunsAsync(secondId, 1, Ct)).Single().Region);
    }

    [TestMethod]
    public async Task AVpnRunHeldUpByASwitchDoesntHoldUpALaterRunWithoutTheVpn()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Gluetun.HoldSwitches = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vpnId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));
        var plainId = await lookout.Client.CreateWatchAsync("Prices", Ct);
        await MakeRotationDueAsync(lookout);
        await lookout.Gluetun.SwitchStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await lookout.Client.QueueRunAsync(vpnId, Ct);
        await lookout.Client.QueueRunAsync(plainId, Ct);
        var plain = (await lookout.Client.WaitForRunsAsync(plainId, 1, Ct)).Single();

        Assert.IsNull(plain.Region);
        Assert.AreEqual(WatchRunStatus.Created, (await lookout.Client.GetRunsAsync(vpnId, Ct)).Single().Status);
        lookout.Gluetun.HoldSwitches.SetResult();
        Assert.AreEqual(Belgium, (await lookout.Client.WaitForRunsAsync(vpnId, 1, Ct)).Single().Region);
    }

    [TestMethod]
    public async Task AWaitingSwitchKeepsNewVpnRunsOutAndWaitsForTheOnesInProgress()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        lookout.Runner.WhileRunning = () => Interlocked.Increment(ref calls) == 1 ? hold.Task : Task.CompletedTask;
        var runningId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));
        var laterId = await lookout.Client.CreateWatchAsync("Prices", Ct, ("UseVpn", "true"));
        await lookout.Client.QueueRunAsync(runningId, Ct);
        await LookoutApi.WaitUntilAsync(() => Task.FromResult(lookout.Runner.InProgress == 1), Ct);

        await MakeRotationDueAsync(lookout);
        await LookoutApi.WaitUntilAsync(() => Task.FromResult(lookout.SwitchProgress is { WaitingForRun: true }), Ct);
        await lookout.Client.QueueRunAsync(laterId, Ct);
        await Task.Delay(SettleTime, Ct);

        Assert.AreEqual(WatchRunStatus.Created, (await lookout.Client.GetRunsAsync(laterId, Ct)).Single().Status);
        Assert.IsEmpty(lookout.Gluetun.SwitchedTo, "The switch waits for the run in progress.");
        hold.SetResult();
        var running = (await lookout.Client.WaitForRunsAsync(runningId, 1, Ct)).Single();
        var later = (await lookout.Client.WaitForRunsAsync(laterId, 1, Ct)).Single();
        Assert.AreEqual(Albania, running.Region);
        Assert.AreEqual("In stock", running.Content);
        Assert.AreEqual(Belgium, later.Region);
    }

    [TestMethod]
    public async Task AFailoverKeepsNewVpnRunsOutWhileRunsInProgressFinishAndASecondOneOnlyRetries()
    {
        await using var lookout = await StartAsync([Albania, Belgium, Chile], current: Albania);
        var holdFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        lookout.Runner.WhileRunning = () => Interlocked.Increment(ref calls) switch
        {
            1 => holdFirst.Task,
            2 => holdSecond.Task,
            _ => Task.CompletedTask,
        };
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        lookout.Runner.Response = Success;
        var firstId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));
        var secondId = await lookout.Client.CreateWatchAsync("Prices", Ct, ("UseVpn", "true"));
        var laterId = await lookout.Client.CreateWatchAsync("Stock", Ct, ("UseVpn", "true"));
        await lookout.Client.QueueRunAsync(firstId, Ct);
        await lookout.Client.QueueRunAsync(secondId, Ct);
        await LookoutApi.WaitUntilAsync(() => Task.FromResult(lookout.Runner.InProgress == 2), Ct);

        // One of them ends in a known failure that asks for a failover, while the other is still running there
        holdFirst.SetResult();
        await LookoutApi.WaitUntilAsync(() => Task.FromResult(lookout.Runner.InProgress == 1), Ct);
        await lookout.Client.QueueRunAsync(laterId, Ct);
        await Task.Delay(SettleTime, Ct);
        Assert.AreEqual(WatchRunStatus.Created, (await lookout.Client.GetRunsAsync(laterId, Ct)).Single().Status);
        Assert.IsEmpty(lookout.Gluetun.SwitchedTo, "The failover waits for the run still in the region.");

        // The other finishes rather than being cut off, and asks for a failover too
        holdSecond.SetResult();
        var first = await lookout.Client.WaitForRunsAsync(firstId, 2, Ct);
        var second = await lookout.Client.WaitForRunsAsync(secondId, 2, Ct);
        var later = (await lookout.Client.WaitForRunsAsync(laterId, 1, Ct)).Single();

        Assert.HasCount(1, lookout.Gluetun.SwitchedTo, "The second failover found the VPN had already left.");
        var newRegion = lookout.Gluetun.SwitchedTo.Single();
        foreach (var runs in new[] { first, second })
        {
            Assert.AreEqual(Albania, runs[0].Region);
            Assert.AreEqual("Blocked", runs[0].FailureReason);
            Assert.IsNull(runs[0].Error);
            Assert.AreEqual(runs[0].Id, runs[1].RetryOfRunId);
            Assert.AreEqual(newRegion, runs[1].Region);
        }

        Assert.AreEqual(newRegion, later.Region);
    }

    [TestMethod]
    public async Task AVpnRunWaitingForASwitchRecordsTheRegionItSwitchedTo()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Gluetun.HoldSwitches = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));
        await lookout.UpdateVpnSettingsAsync(s =>
        {
            s.RotationEnabled = true;
            s.NextRotationTime = DateTime.UtcNow.AddMinutes(-1);
        }, Ct);
        await lookout.Gluetun.SwitchStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        using (await lookout.Client.PostAsync($"/api/Watches/{watchId}/runs", null, Ct))
        {
        }

        lookout.Gluetun.HoldSwitches.SetResult();
        var run = (await lookout.Client.WaitForRunsAsync(watchId, 1, Ct)).Single();

        Assert.AreEqual(Belgium, run.Region);
    }

    [TestMethod]
    public async Task AWatchWithARunAlreadyWaitingIsntRetried()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        lookout.Runner.Response = Success;
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));
        // As when the schedule queues a run while this one fails
        lookout.Runner.WhileRunning = async () =>
        {
            lookout.Runner.WhileRunning = null;
            using var _ = await lookout.Client.PostAsync($"/api/Watches/{watchId}/runs", null, Ct);
        };

        await lookout.Client.RunAsync(watchId, Ct);
        await lookout.Client.WaitForRunsAsync(watchId, 2, Ct);
        var runs = await SettleAsync(lookout, watchId);

        Assert.HasCount(3, runs);
        Assert.IsTrue(runs.All(r => r.RetryOfRunId is null));
        Assert.AreEqual(Belgium, runs[1].Region, "The run already waiting runs in the new region instead.");
    }

    [TestMethod]
    public async Task AFailoverFromARegionThatWasntKnownStillSwitches()
    {
        // gluetun isn't ready at first, so the run starts before the Backend's first switch, in a region of gluetun's
        await using var lookout = await StartAsync([Albania, Belgium], current: null, refuseSwitches: true);
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        lookout.Runner.Response = Success;
        // By the time the run fails, the first switch has landed in that same region
        lookout.Runner.WhileRunning = async () =>
        {
            lookout.Runner.WhileRunning = null;
            lookout.Gluetun.RefuseSwitches = false;
            await lookout.UpdateVpnSettingsAsync(s => s.CurrentRegion = Albania, Ct);
        };
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        await lookout.Client.RunAsync(watchId, Ct);
        var runs = await lookout.Client.WaitForRunsAsync(watchId, 2, Ct);

        Assert.IsNull(runs[0].Region);
        Assert.AreSequenceEqual(new[] { Belgium }, lookout.Gluetun.SwitchedTo.ToArray());
        Assert.AreEqual(Belgium, runs[1].Region);
    }

    [TestMethod]
    public async Task AFailoverWithNoRegionAllowedSaysWhyAndIsntRetried()
    {
        // Not the streak's doing: rotation couldn't leave either
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania, s => s.ExcludedRegions = [Belgium]);
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        lookout.Runner.Response = Success;
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        await lookout.Client.RunAsync(watchId, Ct);
        var runs = await SettleAsync(lookout, watchId);

        Assert.HasCount(2, runs);
        Assert.IsNull(runs[1].RetryOfRunId);
        var settings = await lookout.GetVpnSettingsAsync(Ct);
        Assert.AreEqual(
            "There are no regions to fail over to: every region other than the current one is excluded.", settings.LastError);
    }

    [TestMethod]
    public async Task ASuccessfulFailoverRestartsTheRotationTimer()
    {
        var farOff = DateTime.UtcNow.AddDays(1);
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania, settings =>
        {
            settings.RotationEnabled = true;
            settings.RotationIntervalMinutes = 15;
            settings.RotationJitterMinutes = 0;
            settings.NextRotationTime = farOff;
        });
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        lookout.Runner.Response = Success;
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        await lookout.Client.RunAsync(watchId, Ct);
        await lookout.Client.WaitForRunsAsync(watchId, 2, Ct);

        var settings = await lookout.GetVpnSettingsAsync(Ct);
        Assert.IsLessThan(DateTime.UtcNow.AddMinutes(16), settings.NextRotationTime!.Value);
    }

    /// <summary>
    /// Starts the Backend with gluetun offering <paramref name="regions"/>, and the VPN in <paramref name="current"/>.
    /// </summary>
    /// <param name="refuseSwitches">Whether gluetun refuses switches from the start, as when it isn't ready.</param>
    private async Task<Lookout> StartAsync(
        string[] regions, string? current, Action<VpnSettings>? configure = null, bool refuseSwitches = false)
    {
        var dataDirectory = Directory.CreateTempSubdirectory("lookout-tests-").FullName;
        // Stored before the Backend with the VPN starts, as if it had switched there before a restart
        await using (var before = new LookoutFactory(dataDirectory: dataDirectory))
        {
            await UpdateVpnSettingsAsync(before, settings =>
            {
                settings.CurrentRegion = current;
                configure?.Invoke(settings);
            }, Ct);
        }

        var runner = new FakeRunner(Success);
        var gluetun = new FakeGluetun(current is null ? null : [current]) { RefuseSwitches = refuseSwitches };
        var settings = FakeGluetun.Settings(dataDirectory, string.Join(',', regions), regions);
        // More than the default of one, so VPN runs can overlap, as they do once the Owner raises it
        settings[EnvVars.LookoutMaxConcurrentRuns] = "3";
        var factory = new LookoutFactory(
            dataDirectory: dataDirectory,
            settings: settings,
            configureServices: services =>
            {
                gluetun.Register(services);
                services.AddHttpClient<RunnerHttpClient>().ConfigurePrimaryHttpMessageHandler(() => runner);
            });
        return new Lookout(factory, runner, gluetun, dataDirectory, await factory.CreateSignedInClientAsync(Ct));
    }

    private static async Task UpdateVpnSettingsAsync(
        LookoutFactory factory, Action<VpnSettings> update, CancellationToken cancellationToken)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var settings = await dbContext.VpnSettings.SingleAsync(s => s.Id == VpnSettings.SingletonId, cancellationToken);
        update(settings);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Runs the watch once more and returns all its runs, oldest first, once that run is done. A watch's runs go one at a
    /// time, and a failover is queued before its run is done, so the new run waits for any failover before it.
    /// </summary>
    private async Task<List<WatchRunSummary>> SettleAsync(Lookout lookout, int watchId)
    {
        await lookout.Client.RunAsync(watchId, Ct);
        return await lookout.Client.GetRunsAsync(watchId, Ct);
    }

    private Task MakeRotationDueAsync(Lookout lookout) =>
        lookout.UpdateVpnSettingsAsync(s =>
        {
            s.RotationEnabled = true;
            s.NextRotationTime = DateTime.UtcNow.AddMinutes(-1);
        }, Ct);

    private sealed class Lookout(
        LookoutFactory factory, FakeRunner runner, FakeGluetun gluetun, string dataDirectory, HttpClient client)
        : IAsyncDisposable
    {
        public FakeRunner Runner => runner;
        public FakeGluetun Gluetun => gluetun;
        public HttpClient Client => client;

        public VpnSwitchProgress? SwitchProgress => factory.Services.GetRequiredService<VpnRotationService>().Progress;

        public Task UpdateVpnSettingsAsync(Action<VpnSettings> update, CancellationToken cancellationToken) =>
            VpnFailoverTests.UpdateVpnSettingsAsync(factory, update, cancellationToken);

        public async Task<VpnSettings> GetVpnSettingsAsync(CancellationToken cancellationToken)
        {
            using var scope = factory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            return await dbContext.VpnSettings.AsNoTracking()
                .SingleAsync(s => s.Id == VpnSettings.SingletonId, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await factory.DisposeAsync();
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // SQLite can still hold the file briefly; it's a temp directory
            }
        }
    }
}
