using System.Net.Http.Json;
using ChasePie.Lookout.Backend.Controllers;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Failovers that known failures ask for, and the retries after them, with the Runner and gluetun faked at their HTTP
/// boundaries.
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
        var runs = await WaitForRunsAsync(lookout.Client, watchId, 2);

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
        await WaitForRunsAsync(lookout.Client, watchId, 3);
        var runs = await SettleAsync(lookout, watchId);

        Assert.HasCount(4, runs);
        Assert.AreSequenceEqual(new[] { Belgium, Chile }, lookout.Gluetun.SwitchedTo.Order().ToArray());
        Assert.AreEqual(runs[1].Id, runs[2].RetryOfRunId);
        Assert.IsNull(runs[3].RetryOfRunId, "With no region left, there's no retry.");
        var lastRegion = runs[2].Region!;

        // The success started the streak over, so Albania and the region it first failed over to can be picked again
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        await lookout.Client.RunAsync(watchId, Ct);
        runs = await WaitForRunsAsync(lookout.Client, watchId, 6);

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
        await WaitForRunsAsync(lookout.Client, watchId, 4);
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
        var runs = await WaitForRunsAsync(lookout.Client, watchId, 2);

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
        var runs = await WaitForRunsAsync(lookout.Client, watchId, 2);

        Assert.AreEqual(runs[0].Id, runs[1].RetryOfRunId);
        Assert.HasCount(1, lookout.Gluetun.SwitchedTo);
        Assert.AreEqual(lookout.Gluetun.SwitchedTo.Single(), runs[1].Region);
        Assert.IsNull((await lookout.GetVpnSettingsAsync(Ct)).LastError);
    }

    [TestMethod]
    public async Task AVpnRunQueuedBehindAFailingOneWaitsForTheFailover()
    {
        await using var lookout = await StartAsync([Albania, Belgium], current: Albania);
        lookout.Gluetun.HoldSwitches = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lookout.Runner.Responses.Enqueue(FailoverFailure);
        lookout.Runner.Response = Success;
        var failingId = await lookout.Client.CreateWatchAsync("Tickets", Ct, ("UseVpn", "true"));
        var otherId = await lookout.Client.CreateWatchAsync("Prices", Ct, ("UseVpn", "true"));

        using (await lookout.Client.PostAsync($"/api/Watches/{failingId}/runs", null, Ct))
        using (await lookout.Client.PostAsync($"/api/Watches/{otherId}/runs", null, Ct))
        {
        }

        await lookout.Gluetun.SwitchStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        // Long enough for the worker to have started it, were it not waiting
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        var waiting = (await GetRunsAsync(lookout.Client, otherId, Ct)).Single();
        Assert.AreEqual(WatchRunStatus.Created, waiting.Status);

        lookout.Gluetun.HoldSwitches.SetResult();
        var other = (await WaitForRunsAsync(lookout.Client, otherId, 1)).Single();

        Assert.AreEqual(Belgium, other.Region);
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
        var run = (await WaitForRunsAsync(lookout.Client, watchId, 1)).Single();

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
        await WaitForRunsAsync(lookout.Client, watchId, 2);
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
        var runs = await WaitForRunsAsync(lookout.Client, watchId, 2);

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
        await WaitForRunsAsync(lookout.Client, watchId, 2);

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
        var factory = new LookoutFactory(
            dataDirectory: dataDirectory,
            settings: FakeGluetun.Settings(dataDirectory, string.Join(',', regions), regions),
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
    /// Runs the watch once more and returns all its runs, oldest first, once that run is done. The worker runs one at a
    /// time and finishes a failover before the next run, so by then any failover and retry before it has happened.
    /// </summary>
    private async Task<List<WatchRunSummary>> SettleAsync(Lookout lookout, int watchId)
    {
        await lookout.Client.RunAsync(watchId, Ct);
        return await GetRunsAsync(lookout.Client, watchId, Ct);
    }

    /// <summary>
    /// Waits for the watch to have <paramref name="count"/> runs, all done, and returns them oldest first.
    /// </summary>
    private async Task<List<WatchRunSummary>> WaitForRunsAsync(HttpClient client, int watchId, int count)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            var runs = await GetRunsAsync(client, watchId, timeout.Token);
            if (runs.Count >= count && runs.All(r => r.Status == WatchRunStatus.Done))
            {
                return runs;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }

    private static async Task<List<WatchRunSummary>> GetRunsAsync(
        HttpClient client, int watchId, CancellationToken cancellationToken)
    {
        var newestFirst = await client.GetFromJsonAsync<List<WatchRunSummary>>(
            $"/api/Watches/{watchId}/runs", cancellationToken);
        return [.. newestFirst!.OrderBy(r => r.Id)];
    }

    private sealed class Lookout(
        LookoutFactory factory, FakeRunner runner, FakeGluetun gluetun, string dataDirectory, HttpClient client)
        : IAsyncDisposable
    {
        public FakeRunner Runner => runner;
        public FakeGluetun Gluetun => gluetun;
        public HttpClient Client => client;

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
