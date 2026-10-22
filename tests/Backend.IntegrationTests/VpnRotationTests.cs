using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class VpnRotationTests(TestContext testContext)
{
    [TestMethod]
    public async Task RotatesEveryTenToFifteenMinutes()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var startedAt = factory.Time.GetUtcNow();
        var rotatedAt = new List<DateTimeOffset>();
        factory.Vpn.OnRotate = () =>
        {
            rotatedAt.Add(factory.Time.GetUtcNow());
            factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), $"198.51.100.{rotatedAt.Count}", "Chicago");
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        await Advance(factory, TimeSpan.FromHours(1));

        var gaps = rotatedAt.Prepend(startedAt).Zip(rotatedAt, (previous, next) => next - previous).ToList();
        Assert.IsGreaterThanOrEqualTo(4, gaps.Count);
        foreach (var gap in gaps)
        {
            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMinutes(10), gap);
            // A Rotation can finish a step or two after it starts, and the next is timed from when it finishes.
            Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(15.5), gap);
        }
    }

    [TestMethod]
    public async Task RotatesWhenTheVpnHasNotConnectedFifteenSecondsAfterStartup()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Vpn.Status = VpnStatus.Disconnected("gluetun is connecting the VPN.");
        DateTimeOffset? rotatedAt = null;
        factory.Vpn.OnRotate = () =>
        {
            rotatedAt ??= factory.Time.GetUtcNow();
            // gluetun connects to the server it picks on the attempt
            factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "203.0.113.7", "New York");
        };
        var startedAt = factory.Time.GetUtcNow();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);

        await AdvanceUntil(factory, () => factory.Vpn.Rotations == 1);
        var rotated = await listener.NextVpnStatusAsync(testContext.CancellationToken);
        rotated = await listener.NextVpnStatusAsync(testContext.CancellationToken);

        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(15), rotatedAt!.Value - startedAt);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(17), rotatedAt.Value - startedAt);
        // Any exit IP will do, as there was none before
        Assert.AreEqual("203.0.113.7", rotated.GetProperty("exitIp").GetString());
        Assert.IsFalse(rotated.GetProperty("rotating").GetBoolean());
        Assert.AreEqual(1, factory.Vpn.Rotations);
    }

    [TestMethod]
    public async Task TimesTheCheckAtStartupFromWhenGluetunFirstAnswers()
    {
        await using var factory = new LookoutWebApplicationFactory();
        // Lookout started before gluetun, whose control server isn't up yet
        factory.Vpn.Status = VpnStatus.Unreachable("Could not reach gluetun's control server: Connection refused");
        DateTimeOffset? rotatedAt = null;
        factory.Vpn.OnRotate = () =>
        {
            rotatedAt ??= factory.Time.GetUtcNow();
            factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "203.0.113.7", "New York");
        };
        var startedAt = factory.Time.GetUtcNow();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        await AdvanceUntil(factory, () => factory.Time.GetUtcNow() - startedAt >= TimeSpan.FromSeconds(40));
        var rotationsBeforeGluetunAnswered = factory.Vpn.Rotations;
        var answeredAt = factory.Time.GetUtcNow();
        factory.Vpn.Status = VpnStatus.Disconnected("gluetun is connecting the VPN.");
        await AdvanceUntil(factory, () => factory.Vpn.Rotations == 1);

        Assert.AreEqual(0, rotationsBeforeGluetunAnswered);
        // Checked every 15 seconds, so up to 15 seconds late
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(15), rotatedAt!.Value - answeredAt);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(32), rotatedAt.Value - answeredAt);
    }

    [TestMethod]
    public async Task ARotationWaitsForVpnRunsInProgress()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        factory.Runner.OnExecute = async (_, _) =>
        {
            started.TrySetResult();
            await finish.Task;
            return Succeeded;
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        var running = RunNow(client, watch);
        await started.Task.WaitAsync(testContext.CancellationToken);
        await Advance(factory, TimeSpan.FromMinutes(16));
        var rotationsWhileRunning = factory.Vpn.Rotations;
        finish.SetResult();
        await running;
        await Eventually(() => factory.Vpn.Rotations == 1);

        Assert.AreEqual(0, rotationsWhileRunning);
    }

    [TestMethod]
    public async Task ARotationWaitsNoLongerThanTheLongestRunTimeoutForVpnRuns()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var started = new TaskCompletionSource();
        factory.Runner.OnExecute = async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Succeeded;
        };
        DateTimeOffset? rotatedAt = null;
        factory.Vpn.OnRotate = () =>
        {
            rotatedAt ??= factory.Time.GetUtcNow();
            factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "198.51.100.4", "Chicago");
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        _ = RunNow(client, watch);
        await started.Task.WaitAsync(testContext.CancellationToken);
        var runStartedAt = factory.Time.GetUtcNow();
        await Advance(factory, TimeSpan.FromMinutes(30));

        Assert.IsNotNull(rotatedAt);
        // Due 10–15 minutes in, then waiting 10 minutes for the Run
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMinutes(20), rotatedAt.Value - runStartedAt);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(25.5), rotatedAt.Value - runStartedAt);
    }

    [TestMethod]
    public async Task NoVpnRunStartsDuringARotationWhileDirectRunsCarryOn()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var executed = new List<string>();
        factory.Runner.OnExecute = async (request, _) =>
        {
            lock (executed)
            {
                executed.Add(request.Context.NotificationUrl);
            }
            if (request.Context.NotificationUrl == "https://example.com/first")
            {
                started.TrySetResult();
                await finish.Task;
            }
            return Succeeded;
        };
        factory.Vpn.OnRotate = () => factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "198.51.100.4", "Chicago");
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var first = await CreateWatch(client, usesVpn: true, "https://example.com/first");
        var second = await CreateWatch(client, usesVpn: true, "https://example.com/second");
        var direct = await CreateWatch(client, usesVpn: false, "https://example.com/direct");

        var firstRun = RunNow(client, first);
        await started.Task.WaitAsync(testContext.CancellationToken);
        // Due by now, so the Rotation waits for the first VPN Run
        await Advance(factory, TimeSpan.FromMinutes(16));
        var secondRun = RunNow(client, second);
        // Without fake time moving on, a VPN Run waiting for the VPN would never finish
        var directRun = await RunNow(client, direct).WaitAsync(TimeSpan.FromSeconds(10), testContext.CancellationToken);
        await Task.Delay(200, testContext.CancellationToken);
        string[] executedDuringRotation;
        lock (executed)
        {
            executedDuringRotation = [.. executed];
        }
        finish.SetResult();
        await firstRun;
        var secondRecorded = await secondRun.WaitAsync(TimeSpan.FromSeconds(10), testContext.CancellationToken);

        Assert.AreEqual("https://example.com/first https://example.com/direct", string.Join(' ', executedDuringRotation));
        Assert.AreEqual("succeeded", directRun["outcome"]!.GetValue<string>());
        Assert.AreEqual(1, factory.Vpn.Rotations);
        Assert.AreEqual("succeeded", secondRecorded["outcome"]!.GetValue<string>());
        Assert.AreEqual("198.51.100.4", secondRecorded["vpnExitIp"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ARotationMakesAnotherAttemptUntilTheExitIpChanges()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = Succeeded;
        var original = factory.Vpn.Status;
        factory.Vpn.OnRotate = () => factory.Vpn.Status = factory.Vpn.Rotations switch
        {
            // gluetun can pick the same exit again, or a dead server that never connects
            1 => original,
            2 => VpnStatus.Disconnected("gluetun is connecting the VPN."),
            _ => VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "198.51.100.4", "Chicago")
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        await AdvanceUntil(factory, () => factory.Vpn.Rotations == 1);
        var running = RunNow(client, watch);
        await AdvanceUntil(factory, () => factory.Vpn.Rotations == 2);
        var rotationsBeforeTheThird = factory.Vpn.Rotations;
        await AdvanceUntil(factory, () => running.IsCompleted);
        var run = await running;

        Assert.AreEqual(2, rotationsBeforeTheThird);
        Assert.AreEqual(3, factory.Vpn.Rotations);
        Assert.AreEqual("198.51.100.4", run["vpnExitIp"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ARotationGivesUpWhenTheVpnDoesNotReconnect()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = Succeeded;
        factory.Vpn.OnRotate = () => factory.Vpn.Status = VpnStatus.Disconnected("gluetun is connecting the VPN.");
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        await AdvanceUntil(factory, () => factory.Vpn.Rotations == 1);
        var rotationStartedAt = factory.Time.GetUtcNow();
        var running = RunNow(client, watch);
        await AdvanceUntil(factory, () => running.IsCompleted);
        var run = await running;

        // Refused once the Rotation gives up, after a few attempts, and the VPN still doesn't connect for 2 minutes
        Assert.AreEqual("errored", run["outcome"]!.GetValue<string>());
        Assert.AreEqual("The VPN is not connected, so this VPN Run was not started. gluetun is connecting the VPN.",
            run["errorMessage"]!.GetValue<string>());
        Assert.IsGreaterThan(1, factory.Vpn.Rotations);
        Assert.IsLessThan(TimeSpan.FromMinutes(5), factory.Time.GetUtcNow() - rotationStartedAt);
    }

    [TestMethod]
    public async Task AKnownFailureOnAVpnRunRotatesEarlyWithoutRetryingTheRun()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var executed = new List<RunnerRequest>();
        factory.Runner.OnExecute = (request, _) =>
        {
            executed.Add(request);
            return Task.FromResult(executed.Count == 1 ? KnownFailure : Succeeded);
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        var failed = await RunNow(client, watch);
        await Eventually(() => factory.Vpn.Rotations == 1);
        var executionsAfterRotating = executed.Count;
        var next = await RunNow(client, watch);

        Assert.AreEqual("knownFailure", failed["outcome"]!.GetValue<string>());
        Assert.AreEqual("203.0.113.7", failed["vpnExitIp"]!.GetValue<string>());
        Assert.AreEqual(1, executionsAfterRotating);
        Assert.AreEqual("203.0.113.8", next["vpnExitIp"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task EarlyRotationsComeAtMostOnceEveryThreeMinutes()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = KnownFailure;
        var rotatedAt = new List<DateTimeOffset>();
        factory.Vpn.OnRotate = () =>
        {
            rotatedAt.Add(factory.Time.GetUtcNow());
            factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), $"198.51.100.{rotatedAt.Count}", "Chicago");
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        await RunNow(client, watch);
        await Eventually(() => factory.Vpn.Rotations == 1);
        await AdvanceUntil(factory, () => factory.Time.GetUtcNow() - rotatedAt[0] >= TimeSpan.FromSeconds(30));
        await RunNow(client, watch);
        await RunNow(client, watch);
        await AdvanceUntil(factory, () => factory.Vpn.Rotations == 2);

        // Both later Known failures asked for one, which waited until 3 minutes after the last
        Assert.AreEqual(2, factory.Vpn.Rotations);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMinutes(3), rotatedAt[1] - rotatedAt[0]);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(3), rotatedAt[1] - rotatedAt[0]);
    }

    [TestMethod]
    public async Task AnEarlyRotationThatTheVpnRulesOutIsDropped()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = KnownFailure;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);
        await RunNow(client, watch);
        await Eventually(() => factory.Vpn.Rotations == 1);
        // Asks for another, which waits until 3 minutes after the first
        await RunNow(client, watch);

        factory.Vpn.Status = VpnStatus.MisconfiguredBecause("gluetun refused Lookout's API key (HTTP 401).");
        await Advance(factory, TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(10));
        var reads = factory.Vpn.StatusReads;
        // Without fake time moving on, nothing should read the VPN's status
        await Task.Delay(300, testContext.CancellationToken);

        Assert.AreEqual(reads, factory.Vpn.StatusReads);
        Assert.AreEqual(1, factory.Vpn.Rotations);
    }

    [TestMethod]
    public async Task AKnownFailureOnADirectRunDoesNotRotate()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = KnownFailure;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: false);

        await RunNow(client, watch);
        await Advance(factory, TimeSpan.FromMinutes(5));

        Assert.AreEqual(0, factory.Vpn.Rotations);
    }

    [TestMethod]
    public async Task AVpnTestRunIsRefusedAtOnceDuringARotationAndARotationWaitsForOne()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        factory.Runner.OnExecute = async (_, _) =>
        {
            started.TrySetResult();
            await finish.Task;
            return Succeeded;
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        var testing = TestRun(client, watch);
        await started.Task.WaitAsync(testContext.CancellationToken);
        await Advance(factory, TimeSpan.FromMinutes(16));
        var refused = await TestRun(client, watch).WaitAsync(TimeSpan.FromSeconds(10), testContext.CancellationToken);
        var rotationsWhileTesting = factory.Vpn.Rotations;
        finish.SetResult();
        var tested = await testing;
        await Eventually(() => factory.Vpn.Rotations == 1);

        Assert.AreEqual(0, rotationsWhileTesting);
        Assert.AreEqual("succeeded", tested["outcome"]!.GetValue<string>());
        Assert.AreEqual("errored", refused["outcome"]!.GetValue<string>());
        Assert.AreEqual("The VPN is rotating to a new exit IP, so this Test run was not started. Try again in a minute.",
            refused["errorMessage"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task PushesTheVpnStatusAsARotationStartsAndEnds()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        factory.Runner.OnExecute = async (_, _) =>
        {
            started.TrySetResult();
            await finish.Task;
            return Succeeded;
        };
        factory.Vpn.OnRotate = () => factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "198.51.100.4", "Chicago");
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        var running = RunNow(client, watch);
        await started.Task.WaitAsync(testContext.CancellationToken);
        await Advance(factory, TimeSpan.FromMinutes(16));
        var rotating = await listener.NextVpnStatusAsync(testContext.CancellationToken);
        var duringRotation = await client.GetFromJsonAsync<JsonObject>("/api/vpn", testContext.CancellationToken);
        finish.SetResult();
        await running;
        var rotated = await listener.NextVpnStatusAsync(testContext.CancellationToken);
        var afterRotation = await client.GetFromJsonAsync<JsonObject>("/api/vpn", testContext.CancellationToken);

        Assert.AreEqual(
            """{"enabled":true,"connected":true,"rotating":true,"region":"New York","exitIp":"203.0.113.7","error":null}""",
            rotating.GetRawText());
        Assert.AreEqual(rotating.GetRawText(), duringRotation!.ToJsonString());
        Assert.AreEqual(
            """{"enabled":true,"connected":true,"rotating":false,"region":"Chicago","exitIp":"198.51.100.4","error":null}""",
            rotated.GetRawText());
        Assert.AreEqual(rotated.GetRawText(), afterRotation!.ToJsonString());
    }

    [TestMethod]
    public async Task PushesChangesInTheVpnStatusBetweenRotationsWithinThirtySeconds()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);

        await Advance(factory, TimeSpan.FromSeconds(30));
        factory.Vpn.Status = VpnStatus.Disconnected("gluetun's VPN crashed. gluetun's logs say why.");
        await Advance(factory, TimeSpan.FromSeconds(30));
        var disconnected = await listener.NextVpnStatusAsync(testContext.CancellationToken);
        await Advance(factory, TimeSpan.FromSeconds(60));
        factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "198.51.100.4", "Chicago");
        await Advance(factory, TimeSpan.FromSeconds(30));
        var reconnected = await listener.NextVpnStatusAsync(testContext.CancellationToken);

        // Nothing for the unchanged status in between, or before the first change
        Assert.AreEqual(
            """{"enabled":true,"connected":false,"rotating":false,"region":null,"exitIp":null,"error":"gluetun's VPN crashed. gluetun's logs say why."}""",
            disconnected.GetRawText());
        Assert.AreEqual(
            """{"enabled":true,"connected":true,"rotating":false,"region":"Chicago","exitIp":"198.51.100.4","error":null}""",
            reconnected.GetRawText());
    }

    [TestMethod]
    public async Task RefreshesTheServerListWhenTheVpnFirstConnectsAtMostOnceEvery360Hours()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Vpn.Status = VpnStatus.Disconnected("gluetun is connecting the VPN.");
        // gluetun stays on a dead server through the Rotation at startup
        factory.Vpn.OnRotate = () => { };
        var databasePath = Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");
        try
        {
            int whileDisconnected, onceConnected, laterThatRun;
            await using (var first = SharingDatabase(factory, databasePath))
            {
                using var client = await first.CreateSignedInClientAsync(testContext.CancellationToken);
                await Advance(factory, TimeSpan.FromMinutes(1));
                whileDisconnected = factory.Vpn.ServerListRefreshes;
                factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "203.0.113.7", "New York");
                await Advance(factory, TimeSpan.FromSeconds(30));
                await Eventually(() => factory.Vpn.ServerListRefreshes > whileDisconnected);
                onceConnected = factory.Vpn.ServerListRefreshes;
                await Advance(factory, TimeSpan.FromHours(1));
                laterThatRun = factory.Vpn.ServerListRefreshes;
            }

            factory.Time.Advance(TimeSpan.FromHours(300));
            int afterARestartWithin360Hours;
            await using (var second = SharingDatabase(factory, databasePath))
            {
                using var client = second.CreateClient();
                await Advance(factory, TimeSpan.FromMinutes(1));
                afterARestartWithin360Hours = factory.Vpn.ServerListRefreshes;
            }

            factory.Time.Advance(TimeSpan.FromHours(60));
            await using (var third = SharingDatabase(factory, databasePath))
            {
                using var client = third.CreateClient();
                await Advance(factory, TimeSpan.FromMinutes(1));
                await Eventually(() => factory.Vpn.ServerListRefreshes == 2);
            }

            Assert.AreEqual(0, whileDisconnected);
            Assert.AreEqual(1, onceConnected);
            Assert.AreEqual(1, laterThatRun);
            Assert.AreEqual(1, afterARestartWithin360Hours);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static WebApplicationFactory<Program> SharingDatabase(LookoutWebApplicationFactory factory, string databasePath) =>
        factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Lookout", $"Data Source={databasePath};Pooling=False"));

    [TestMethod]
    public async Task DoesNotRotateOrPushWithoutAVpn()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Vpn.Status = VpnStatus.Disabled;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);

        await Advance(factory, TimeSpan.FromMinutes(30));

        Assert.AreEqual(0, factory.Vpn.Rotations);
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => listener.NextVpnStatusAsync(testContext.CancellationToken));
    }

    private async Task<JsonObject> TestRun(HttpClient client, string id)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("export default { async run() { return { success: true, content: '£21' }; } };"), "script", "price.ts" }
        };
        var response = await client.PostAsync(new Uri($"/api/watches/{id}/test-runs", UriKind.Relative), form, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
    }

    private static readonly RunnerResult KnownFailure = new() { Outcome = RunOutcome.KnownFailure, Details = "Access denied", DurationMs = 1 };

    private static readonly RunnerResult Succeeded = new() { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 };

    private async Task<string> CreateWatch(HttpClient client, bool usesVpn, string notificationUrl = "https://example.com/widget")
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Widget price"), "name" },
            { new StringContent(notificationUrl), "notificationUrl" },
            { new StringContent("export default { async run() { return { success: true, content: '£20' }; } };"), "script", "price.ts" },
            { new StringContent("interval"), "scheduleKind" },
            { new StringContent("1"), "every" },
            { new StringContent("days"), "unit" },
            { new StringContent(usesVpn ? "true" : "false"), "usesVpn" }
        };
        var response = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["id"]!.GetValue<string>();
    }

    private async Task<JsonObject> RunNow(HttpClient client, string id)
    {
        var response = await client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
    }

    /// <summary>Waits in real time for background work to get somewhere, without moving fake time.</summary>
    private async Task Eventually(Func<bool> done)
    {
        for (var attempt = 0; !done(); attempt++)
        {
            Assert.IsLessThan(500, attempt, "It never got there.");
            await Task.Delay(10, testContext.CancellationToken);
        }
    }

    /// <summary>Moves fake time on a second at a time, letting background work react to each, until <paramref name="done"/> holds.</summary>
    private async Task AdvanceUntil(LookoutWebApplicationFactory factory, Func<bool> done)
    {
        for (var step = 0; !done(); step++)
        {
            Assert.IsLessThan(3600, step, "It never got there.");
            factory.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2, testContext.CancellationToken);
        }
    }

    /// <summary>Moves fake time on in small steps, letting background work react to each.</summary>
    private async Task Advance(LookoutWebApplicationFactory factory, TimeSpan by)
    {
        for (var moved = TimeSpan.Zero; moved < by; moved += Step)
        {
            factory.Time.Advance(Step);
            await Task.Delay(5, testContext.CancellationToken);
        }
    }

    private static readonly TimeSpan Step = TimeSpan.FromSeconds(10);
}
