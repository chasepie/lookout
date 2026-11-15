using ChasePie.Lookout.Backend.Controllers;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// What the run worker does with the VPN: which runs ask it for a failover, the regions they pass it from the watch's
/// known-failure streak, the retries after a failover, and how VPN runs wait for switches, with the Runner and gluetun
/// faked at their HTTP boundaries. The VPN's own rules are in <see cref="VpnTests"/>.
/// </summary>
[TestClass]
public sealed class VpnFailoverTests
{
    private const string Albania = "Albania";
    private const string Belgium = "Belgium";
    private const string Chile = "Chile";

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
    public async Task AFailoverAvoidsTheRegionsTheStreakFailedInUntilASuccess()
    {
        await using var lookout = await StartAsync([Albania, Belgium, Chile], current: Albania);
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
        Assert.AreEqual(runs[4].Id, runs[5].RetryOfRunId);
    }

    [TestMethod]
    public async Task AFailoverThatDidntLeaveTheRegionQueuesNoRetry()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Gluetun.RefuseSwitches = true;
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        await lookout.Client.RunAsync(watchId, Ct);
        var runs = await SettleAsync(lookout, watchId);

        Assert.HasCount(2, runs);
        Assert.IsTrue(runs[0].AskedForFailover);
        Assert.IsNull(runs[1].RetryOfRunId);
        Assert.AreEqual(Albania, runs[1].Region);
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

    /// <summary>
    /// Starts the Backend with gluetun offering <paramref name="regions"/>, and the VPN in <paramref name="current"/>.
    /// </summary>
    private async Task<Lookout> StartAsync(string[] regions, string current)
    {
        var dataDirectory = Directory.CreateTempSubdirectory("lookout-tests-").FullName;
        // Stored before the Backend with the VPN starts, as if it had switched there before a restart
        await using (var before = new LookoutFactory(dataDirectory: dataDirectory))
        {
            await UpdateVpnSettingsAsync(before, settings => settings.CurrentRegion = current, Ct);
        }

        var runner = new FakeRunner(Success);
        var gluetun = new FakeGluetun([current]);
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

        public Task UpdateVpnSettingsAsync(Action<VpnSettings> update, CancellationToken cancellationToken) =>
            VpnFailoverTests.UpdateVpnSettingsAsync(factory, update, cancellationToken);

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
