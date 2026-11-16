using System.Net;
using System.Net.Http.Json;
using ChasePie.Lookout.Backend.Controllers;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// What the run worker does with avoided regions: which runs make their regions avoided, the retries after them, and how
/// a watch's runs leave the regions it avoids, with the Runner and gluetun faked at their HTTP boundaries, on the real
/// clock. The VPN's own rules, and how the worker fits switches around VPN runs, are in <see cref="VpnTests"/>.
/// </summary>
[TestClass]
public sealed class AvoidedRegionsTests
{
    private const string Albania = "Albania";
    private const string Belgium = "Belgium";
    private const string Chile = "Chile";

    private static readonly object AvoidingFailure = new { success = false, failureReason = "Blocked", avoidRegion = true };
    private static readonly object PlainFailure = new { success = false, failureReason = "Sold out" };
    private static readonly object Success = new { success = true, content = "In stock" };
    private static readonly object Crash = new { crash = new { error = "Error: No price" } };

    // Long enough for the worker to have started a run or a switch, were it going to
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task AKnownFailureAskingToAvoidItsRegionAvoidsItAndRetriesTheWatchElsewhere()
    {
        // Rotation is off, which doesn't stop the switch
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Runner.Responses.Enqueue(AvoidingFailure);
        lookout.Runner.Response = Success;
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        var failed = await lookout.Client.RunAsync(watchId, Ct);
        var avoided = await GetAvoidedRegionsAsync(lookout.Client, watchId);
        var runs = await lookout.Client.WaitForRunsAsync(watchId, 2, Ct);

        Assert.AreEqual(Albania, failed.Region);
        Assert.IsTrue(failed.AskedToAvoidRegion);
        Assert.AreEqual("Blocked", failed.FailureReason);
        Assert.IsNull(failed.Event, "It's still a known failure, below the threshold.");
        Assert.AreEqual(Albania, avoided.Single().Region);
        Assert.AreEqual(failed.Id, avoided.Single().RunId);
        var retry = runs[1];
        Assert.AreEqual(failed.Id, retry.RetryOfRunId);
        Assert.AreEqual(Belgium, retry.Region);
        Assert.AreEqual("In stock", retry.Content);
        Assert.AreSequenceEqual(new[] { Belgium }, lookout.Gluetun.SwitchedTo.ToArray());
    }

    [TestMethod]
    [DataRow("plain known failure")]
    [DataRow("success")]
    [DataRow("crash")]
    public async Task OnlyAKnownFailureAskingToAvoidItsRegionAvoidsIt(string outcome)
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
        Assert.IsFalse(runs[0].AskedToAvoidRegion);
        Assert.IsNull(runs[1].RetryOfRunId);
        Assert.IsEmpty(await GetAvoidedRegionsAsync(lookout.Client, watchId));
        Assert.IsEmpty(lookout.Gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task AWatchWithoutTheVpnOnlyRecordsItsRequest()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Runner.Responses.Enqueue(AvoidingFailure);
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct);

        await lookout.Client.RunAsync(watchId, Ct);
        var runs = await SettleAsync(lookout, watchId);

        Assert.HasCount(2, runs);
        Assert.IsTrue(runs[0].AskedToAvoidRegion);
        Assert.IsNull(runs[0].Region);
        Assert.IsNull(runs[1].RetryOfRunId);
        Assert.IsEmpty(await GetAvoidedRegionsAsync(lookout.Client, watchId));
        Assert.IsEmpty(lookout.Gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task ARegionStaysAvoidedAfterASuccessUntilTheOwnerClearsIt()
    {
        await using var lookout = await StartAsync([Albania, Belgium, Chile], current: Albania);
        lookout.Runner.Responses.Enqueue(AvoidingFailure);
        lookout.Runner.Response = Success;
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));
        await lookout.Client.RunAsync(watchId, Ct);
        await lookout.Client.WaitForRunsAsync(watchId, 2, Ct);

        // Back in the region, the watch's next run switches away again, success or not
        await SwitchToAsync(lookout, Albania);
        var leftAgain = await lookout.Client.RunAsync(watchId, Ct);
        Assert.AreNotEqual(Albania, leftAgain.Region);

        var avoided = (await GetAvoidedRegionsAsync(lookout.Client, watchId)).Single();
        using (var response = await lookout.Client.DeleteAsync(
            $"/api/Watches/{watchId}/avoided-regions/{avoided.Id}", Ct))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.IsEmpty(await GetAvoidedRegionsAsync(lookout.Client, watchId));
        await SwitchToAsync(lookout, Albania);
        Assert.AreEqual(Albania, (await lookout.Client.RunAsync(watchId, Ct)).Region);
    }

    [TestMethod]
    public async Task ClearingARegionTheWatchDoesntAvoidIsNotFound()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        var otherId = await lookout.Client.CreateWatchAsync("Prices", Ct, ("UseVpn", "true"));
        await lookout.AvoidAsync(otherId, Albania);
        var othersId = (await GetAvoidedRegionsAsync(lookout.Client, otherId)).Single().Id;

        using var response = await lookout.Client.DeleteAsync(
            $"/api/Watches/{watchId}/avoided-regions/{othersId}", Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.HasCount(1, await GetAvoidedRegionsAsync(lookout.Client, otherId));
    }

    [TestMethod]
    public async Task OtherWatchesKeepRunningInARegionAWatchAvoids()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        var avoidingId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));
        var otherId = await lookout.Client.CreateWatchAsync("Prices", Ct, ("UseVpn", "true"));
        await lookout.AvoidAsync(avoidingId, Albania);

        var other = await lookout.Client.RunAsync(otherId, Ct);
        await Task.Delay(SettleTime, Ct);

        Assert.AreEqual(Albania, other.Region);
        Assert.IsEmpty(lookout.Gluetun.SwitchedTo, "Only the avoiding watch's run asks for a switch.");
        Assert.AreEqual(Belgium, (await lookout.Client.RunAsync(avoidingId, Ct)).Region);
    }

    [TestMethod]
    public async Task ARunOfAWatchThatAvoidsEveryRegionLeftCrashesWithoutRunningItsScript()
    {
        await using var lookout = await StartAsync(
            [Albania, Belgium, Chile], current: Albania, settings => settings.ExcludedRegions = [Chile]);
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));
        await lookout.AvoidAsync(watchId, Albania);
        await lookout.AvoidAsync(watchId, Belgium);

        var run = await lookout.Client.RunAsync(watchId, Ct);

        Assert.AreEqual(WatchRunNoRegionLeftError, run.Error);
        Assert.IsNull(run.Region);
        Assert.IsEmpty(lookout.Runner.Requests);
        Assert.IsEmpty(lookout.Gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task AWatchIsRetriedEvenWithNoRegionLeftAndTheRetryCrashes()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Runner.Responses.Enqueue(AvoidingFailure);
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));
        await lookout.AvoidAsync(watchId, Belgium);

        await lookout.Client.RunAsync(watchId, Ct);
        var runs = await lookout.Client.WaitForRunsAsync(watchId, 2, Ct);

        Assert.AreEqual(runs[0].Id, runs[1].RetryOfRunId);
        Assert.AreEqual(WatchRunNoRegionLeftError, runs[1].Error);
        Assert.HasCount(1, lookout.Runner.Requests);
    }

    [TestMethod]
    public async Task AThirdRetryInARowStillAvoidsItsRegionButIsntRetried()
    {
        await using var lookout = await StartAsync(["A", "B", "C", "D", "E", "F"], current: "A");
        for (var i = 0; i < 4; i++)
        {
            lookout.Runner.Responses.Enqueue(AvoidingFailure);
        }

        lookout.Runner.Response = Success;
        var watchId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));

        await lookout.Client.RunAsync(watchId, Ct);
        await lookout.Client.WaitForRunsAsync(watchId, 4, Ct);
        var runs = await SettleAsync(lookout, watchId);

        Assert.HasCount(5, runs);
        Assert.AreEqual(runs[2].Id, runs[3].RetryOfRunId);
        Assert.IsNull(runs[4].RetryOfRunId);
        Assert.HasCount(4, await GetAvoidedRegionsAsync(lookout.Client, watchId));
        Assert.HasCount(4, lookout.Gluetun.SwitchedTo);
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
        lookout.Runner.Responses.Enqueue(AvoidingFailure);
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
        Assert.AreEqual(Belgium, runs[1].Region, "The run already waiting runs in another region instead.");
    }

    /// <summary>
    /// Starts the Backend with gluetun offering <paramref name="regions"/>, and the VPN in <paramref name="current"/>.
    /// </summary>
    private async Task<Lookout> StartAsync(
        string[] regions, string current, Action<VpnSettings>? configure = null)
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
    /// time, and a retry is saved with the run it retries, so the new run comes after any retry.
    /// </summary>
    private async Task<List<WatchRunSummary>> SettleAsync(Lookout lookout, int watchId)
    {
        await lookout.Client.RunAsync(watchId, Ct);
        return await lookout.Client.GetRunsAsync(watchId, Ct);
    }

    private const string WatchRunNoRegionLeftError = WatchRunWorker.NoRegionLeftError;

    private async Task<IReadOnlyList<AvoidedRegionSummary>> GetAvoidedRegionsAsync(HttpClient client, int watchId) =>
        (await client.GetFromJsonAsync<WatchSummary>($"/api/Watches/{watchId}", Ct))!.AvoidedRegions;

    /// <summary>
    /// Switches the VPN to <paramref name="region"/> as the Owner would, and waits until it's there.
    /// </summary>
    private async Task SwitchToAsync(Lookout lookout, string region)
    {
        using (var response = await lookout.Client.PostAsJsonAsync("/api/Vpn/switch", new { region }, Ct))
        {
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        }

        await LookoutApi.WaitUntilAsync(
            async () => (await lookout.GetVpnSettingsAsync(Ct)).CurrentRegion == region && lookout.Vpn.Progress is null, Ct);
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

        public Vpn Vpn => factory.Services.GetRequiredService<Vpn>();

        public Task UpdateVpnSettingsAsync(Action<VpnSettings> update, CancellationToken cancellationToken) =>
            AvoidedRegionsTests.UpdateVpnSettingsAsync(factory, update, cancellationToken);

        public async Task<VpnSettings> GetVpnSettingsAsync(CancellationToken cancellationToken)
        {
            using var scope = factory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            return await dbContext.VpnSettings.AsNoTracking()
                .SingleAsync(s => s.Id == VpnSettings.SingletonId, cancellationToken);
        }

        /// <summary>
        /// Makes <paramref name="region"/> one the watch avoids, as an earlier run's known failure would have.
        /// </summary>
        public async Task AvoidAsync(int watchId, string region)
        {
            using var scope = factory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            dbContext.AvoidedRegions.Add(
                new AvoidedRegion { WatchId = watchId, Region = region, CreatedTime = DateTime.UtcNow });
            await dbContext.SaveChangesAsync();
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
