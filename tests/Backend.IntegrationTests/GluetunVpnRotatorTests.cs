using System.Collections.Concurrent;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;
using ChasePie.Lookout.Model;
using Microsoft.Extensions.Time.Testing;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class GluetunVpnRotatorTests(TestContext testContext)
{
    private const string ApiKey = "control-key";

    [TestMethod]
    public async Task ReportsTheExitOfARunningVpn()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        var rotator = gluetun.CreateRotator(ApiKey);

        var status = await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.AreEqual(VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "198.51.100.23", "Florida"), status);
    }

    [TestMethod]
    [DataRow("running", "", "gluetun is connecting the VPN.", DisplayName = "Running before its tunnel is up, which gluetun reports with no public IP")]
    [DataRow("stopped", "198.51.100.23", "gluetun's VPN is stopped.", DisplayName = "Stopped")]
    [DataRow("crashed", "198.51.100.23", "gluetun's VPN crashed. gluetun's logs say why.", DisplayName = "Crashed")]
    public async Task SaysWhyTheVpnIsDisconnectedUnlessItIsRunningWithAPublicIp(string vpnStatus, string publicIp, string error)
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        gluetun.VpnStatus = vpnStatus;
        gluetun.PublicIp = $$"""{"public_ip":"{{publicIp}}","region":"Florida"}""";
        var rotator = gluetun.CreateRotator(ApiKey);

        var status = await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.AreEqual(VpnStatus.Disconnected(error), status);
    }

    [TestMethod]
    public async Task CountsAnExitIpFetchedThroughTheProxyWhenGluetunHasHadNoneForTenSeconds()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        // gluetun's own lookup of its public IP failed, though the tunnel is up
        gluetun.PublicIp = """{"public_ip":""}""";
        var time = new FakeTimeProvider();
        var checks = new ConcurrentQueue<RunnerRequest>();
        var runner = new FakeRunnerClient
        {
            OnExecute = (request, _) =>
            {
                checks.Enqueue(request);
                return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "198.51.100.50\n", DurationMs = 1 });
            }
        };
        var rotator = gluetun.CreateRotator(ApiKey, time, runner);

        var first = await rotator.GetStatusAsync(testContext.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(9));
        var beforeTenSeconds = await rotator.GetStatusAsync(testContext.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        var afterTenSeconds = await rotator.GetStatusAsync(testContext.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(9));
        var nineSecondsAfterTheCheck = await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.AreEqual(VpnStatus.Disconnected("gluetun is connecting the VPN."), first);
        Assert.AreEqual(VpnStatus.Disconnected("gluetun is connecting the VPN."), beforeTenSeconds);
        // gluetun's lookup gives the region, which the proxy's doesn't
        var connected = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "198.51.100.50", null);
        Assert.AreEqual(connected, afterTenSeconds);
        // Every read until the next check sees the exit the check found
        Assert.AreEqual(connected, nineSecondsAfterTheCheck);
        Assert.AreEqual(new ProxySettings("http://gluetun:8888"), checks.Single().Proxy);
    }

    [TestMethod]
    public async Task ACheckThroughTheProxyIsNotHeldToTheStatusDeadline()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        gluetun.PublicIp = """{"public_ip":""}""";
        var time = new FakeTimeProvider();
        var runner = new FakeRunnerClient
        {
            OnExecute = (_, cancellationToken) =>
            {
                // Browserless takes longer to start a session than gluetun's deadline allows
                time.Advance(TimeSpan.FromSeconds(3));
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "198.51.100.50", DurationMs = 3000 });
            }
        };
        var rotator = gluetun.CreateRotator(ApiKey, time, runner, settings => settings with { StatusTimeout = TimeSpan.FromSeconds(2) });

        await rotator.GetStatusAsync(testContext.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));
        var status = await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.AreEqual(VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "198.51.100.50", null), status);
    }

    [TestMethod]
    public async Task ARotationForgetsTheExitFoundThroughTheProxy()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        gluetun.PublicIp = """{"public_ip":""}""";
        var time = new FakeTimeProvider();
        var checks = 0;
        var runner = new FakeRunnerClient
        {
            OnExecute = (_, _) =>
            {
                Interlocked.Increment(ref checks);
                return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "198.51.100.50", DurationMs = 1 });
            }
        };
        var rotator = gluetun.CreateRotator(ApiKey, time, runner);
        await rotator.GetStatusAsync(testContext.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));
        await rotator.GetStatusAsync(testContext.CancellationToken);
        // Long enough since that check for another to be due
        time.Advance(TimeSpan.FromSeconds(10));

        await rotator.RotateAsync(testContext.CancellationToken);
        var justAfter = await rotator.GetStatusAsync(testContext.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(9));
        await rotator.GetStatusAsync(testContext.CancellationToken);
        var checksWithinTenSeconds = checks;

        // The old exit is gone, and the new tunnel gets 10 seconds for gluetun to find its exit before a check
        Assert.AreEqual(VpnStatus.Disconnected("gluetun is connecting the VPN."), justAfter);
        Assert.AreEqual(1, checksWithinTenSeconds);
    }

    [TestMethod]
    [DataRow(RunOutcome.Errored, null, DisplayName = "The page didn't load, as the tunnel is down")]
    [DataRow(RunOutcome.Succeeded, "<html>Rate limited</html>", DisplayName = "The page wasn't an IP")]
    [DataRow(RunOutcome.Succeeded, "429", DisplayName = "The page was a number, which IPAddress would read as an IPv4 address")]
    [DataRow(RunOutcome.Succeeded, "1.2.3", DisplayName = "The page was a short dotted number")]
    public async Task ChecksThroughTheProxyAtMostEveryTenSecondsUntilOneFindsAnExitIp(RunOutcome outcome, string? content)
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        gluetun.PublicIp = """{"public_ip":""}""";
        var time = new FakeTimeProvider();
        var checks = 0;
        var runner = new FakeRunnerClient
        {
            OnExecute = (_, _) =>
            {
                Interlocked.Increment(ref checks);
                return Task.FromResult(new RunnerResult { Outcome = outcome, Content = content, DurationMs = 1 });
            }
        };
        var rotator = gluetun.CreateRotator(ApiKey, time, runner);

        await rotator.GetStatusAsync(testContext.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));
        var failed = await rotator.GetStatusAsync(testContext.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(9));
        await rotator.GetStatusAsync(testContext.CancellationToken);
        var checksWithinTenSeconds = checks;
        time.Advance(TimeSpan.FromSeconds(1));
        await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.AreEqual(VpnStatus.Disconnected("gluetun is connecting the VPN."), failed);
        Assert.AreEqual(1, checksWithinTenSeconds);
        Assert.AreEqual(2, checks);
    }

    [TestMethod]
    public async Task IsMisconfiguredWhenGluetunRefusesTheApiKey()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        var rotator = gluetun.CreateRotator("wrong-key");

        var status = await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.AreEqual(
            VpnStatus.MisconfiguredBecause("gluetun refused Lookout's API key (HTTP 401). Check that Vpn:ApiKey matches gluetun's."),
            status);
    }

    [TestMethod]
    public async Task IsDisconnectedWhenGluetunCantBeReached()
    {
        Uri address;
        await using (var gluetun = await FakeGluetun.StartAsync(ApiKey))
        {
            address = gluetun.Address;
        }
        using var http = new HttpClient { BaseAddress = address };
        var rotator = new GluetunVpnRotator(http, new GluetunSettings(ApiKey, new ProxySettings("http://gluetun:8888")),
            new ProxiedExitIp(() => new FakeRunnerClient(), TimeProvider.System), TimeProvider.System);

        var status = await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.IsFalse(status.Connected);
        Assert.IsTrue(status.Enabled);
        Assert.IsFalse(status.Reached);
        Assert.StartsWith("Could not reach gluetun's control server: ", status.Error!);
    }

    [TestMethod]
    public async Task RotatesByStoppingThenStartingTheVpn()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        var rotator = gluetun.CreateRotator(ApiKey);

        await rotator.RotateAsync(testContext.CancellationToken);

        Assert.AreSequenceEqual(
            ["""PUT /v1/vpn/status {"status":"stopped"}""", """PUT /v1/vpn/status {"status":"running"}"""],
            gluetun.Changes.ToList());
    }

    [TestMethod]
    public async Task RetriesStartingTheVpnSoARotationDoesNotLeaveItStopped()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        gluetun.FailingStarts = 2;
        var rotator = gluetun.CreateRotator(ApiKey);

        await rotator.RotateAsync(testContext.CancellationToken);

        Assert.AreSequenceEqual(
            [
                """PUT /v1/vpn/status {"status":"stopped"}""",
                """PUT /v1/vpn/status {"status":"running"}""",
                """PUT /v1/vpn/status {"status":"running"}""",
                """PUT /v1/vpn/status {"status":"running"}"""
            ],
            gluetun.Changes.ToList());
    }

    [TestMethod]
    public async Task ARotationFailsWhenGluetunWontStartTheVpnInThreeAttempts()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        gluetun.FailingStarts = 3;
        var rotator = gluetun.CreateRotator(ApiKey);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => rotator.RotateAsync(testContext.CancellationToken));

        Assert.HasCount(4, gluetun.Changes);
    }

    [TestMethod]
    public async Task ARotationGluetunRefusesFails()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        var rotator = gluetun.CreateRotator("wrong-key");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => rotator.RotateAsync(testContext.CancellationToken));
        Assert.IsEmpty(gluetun.Changes);
    }

    [TestMethod]
    public async Task RefreshesTheServerListThroughGluetunsUpdaterAndWaitsForItToComplete()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        gluetun.UpdaterGoesThrough("running", "running", "completed");
        var rotator = gluetun.CreateRotator(ApiKey, configure: QuickUpdaterChecks);

        var completed = await rotator.RefreshServerListAsync(testContext.CancellationToken);

        Assert.IsTrue(completed);
        Assert.AreSequenceEqual(["""PUT /v1/updater/status {"status":"running"}"""], gluetun.Changes.ToList());
        Assert.AreEqual(0, gluetun.UpdaterStatusesLeft);
    }

    [TestMethod]
    [DataRow("crashed")]
    [DataRow("stopped")]
    public async Task ARefreshTheUpdaterDoesNotCompleteFails(string ended)
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        gluetun.UpdaterGoesThrough("running", ended);
        var rotator = gluetun.CreateRotator(ApiKey, configure: QuickUpdaterChecks);

        var completed = await rotator.RefreshServerListAsync(testContext.CancellationToken);

        Assert.IsFalse(completed);
    }

    [TestMethod]
    public async Task ARefreshThatRunsTooLongFails()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        gluetun.UpdaterGoesThrough("running");
        var rotator = gluetun.CreateRotator(ApiKey,
            configure: settings => QuickUpdaterChecks(settings) with { UpdaterTimeout = TimeSpan.FromMilliseconds(300) });

        var completed = await rotator.RefreshServerListAsync(testContext.CancellationToken);

        Assert.IsFalse(completed);
    }

    private static GluetunSettings QuickUpdaterChecks(GluetunSettings settings) =>
        settings with { UpdaterCheckInterval = TimeSpan.FromMilliseconds(20) };

    [TestMethod]
    public async Task ARefreshGluetunRefusesFails()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        var rotator = gluetun.CreateRotator("wrong-key");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => rotator.RefreshServerListAsync(testContext.CancellationToken));
        Assert.IsEmpty(gluetun.Changes);
    }

    [TestMethod]
    public async Task IsDisconnectedWhenGluetunDropsItsResponse()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        gluetun.DropsPublicIpResponse = true;
        var rotator = gluetun.CreateRotator(ApiKey);

        var status = await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.IsFalse(status.Connected);
        Assert.StartsWith("Could not reach gluetun's control server: ", status.Error!);
    }

    [TestMethod]
    public async Task GivesUpOnAnUnresponsiveGluetunWithinOneDeadlineForTheWholeStatus()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        gluetun.VpnStatusDelay = TimeSpan.FromMilliseconds(400);
        gluetun.PublicIpDelay = TimeSpan.FromMilliseconds(400);
        using var http = new HttpClient { BaseAddress = gluetun.Address };
        var rotator = new GluetunVpnRotator(http,
            new GluetunSettings(ApiKey, new ProxySettings("http://gluetun:8888")) { StatusTimeout = TimeSpan.FromMilliseconds(600) },
            new ProxiedExitIp(() => new FakeRunnerClient(), TimeProvider.System), TimeProvider.System);

        var status = await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.AreEqual(VpnStatus.Unreachable("gluetun's control server did not respond within 0.6 seconds."), status);
    }
}
