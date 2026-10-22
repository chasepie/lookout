using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;

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
    [DataRow("running", "", DisplayName = "Running before its tunnel is up, which gluetun reports with no public IP")]
    [DataRow("stopped", "198.51.100.23", DisplayName = "Stopped")]
    [DataRow("crashed", "198.51.100.23", DisplayName = "Crashed")]
    public async Task IsDisconnectedUnlessTheVpnIsRunningWithAPublicIp(string vpnStatus, string publicIp)
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        gluetun.VpnStatus = vpnStatus;
        gluetun.PublicIp = $$"""{"public_ip":"{{publicIp}}","region":"Florida"}""";
        var rotator = gluetun.CreateRotator(ApiKey);

        var status = await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.AreEqual(VpnStatus.Disconnected(), status);
    }

    [TestMethod]
    public async Task IsDisconnectedWhenGluetunRefusesTheApiKey()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        var rotator = gluetun.CreateRotator("wrong-key");

        var status = await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.AreEqual(
            VpnStatus.Disconnected("gluetun refused Lookout's API key (HTTP 401). Check that Vpn:ApiKey matches gluetun's."),
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
            TimeProvider.System);

        var status = await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.IsFalse(status.Connected);
        Assert.IsTrue(status.Enabled);
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
    public async Task ARotationGluetunRefusesFails()
    {
        await using var gluetun = await FakeGluetun.StartAsync(ApiKey);
        var rotator = gluetun.CreateRotator("wrong-key");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => rotator.RotateAsync(testContext.CancellationToken));
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
            TimeProvider.System);

        var status = await rotator.GetStatusAsync(testContext.CancellationToken);

        Assert.AreEqual(VpnStatus.Disconnected("gluetun's control server did not respond within 0.6 seconds."), status);
    }
}
