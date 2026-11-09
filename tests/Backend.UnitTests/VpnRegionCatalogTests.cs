using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.VpnServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChasePie.Lookout.Backend.UnitTests;

[TestClass]
public sealed class VpnRegionCatalogTests
{
    private const string Pia = "private internet access";

    private static readonly string[] BerlinAndTexas = ["DE Berlin", "US Texas"];
    private static readonly string[] BerlinAndLowerCaseTexas = ["DE Berlin", "us texas"];
    private static readonly string[] Texas = ["US Texas"];
    private static readonly string[] Berlin = ["DE Berlin"];
    private static readonly string[] Iceland = ["Iceland"];

    private string _dataDirectory = null!;

    public TestContext TestContext { get; set; } = null!;

    private string ServersFile => Path.Join(_dataDirectory, "servers.json");

    [TestInitialize]
    public void CreateDataDirectory()
    {
        _dataDirectory = Path.Join(Path.GetTempPath(), "lookout-vpn-region-catalog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
    }

    [TestCleanup]
    public void DeleteDataDirectory() => Directory.Delete(_dataDirectory, recursive: true);

    [TestMethod]
    public async Task ReadsTheRegionsOfTheProviderGluetunUses()
    {
        WriteServers(new()
        {
            [Pia] = [Server("openvpn", "US Texas"), Server("openvpn", "DE Berlin")],
            ["nordvpn"] = [Server("openvpn", "Iceland")],
        });

        var regions = await GetRegionsAsync(Pia, "openvpn");

        Assert.AreEqual(Pia, regions.Provider);
        Assert.AreSequenceEqual(BerlinAndTexas, regions.Regions);
    }

    [TestMethod]
    public async Task ListsEachRegionOnceSortedIgnoringCase()
    {
        WriteServers(new()
        {
            [Pia] = [Server("openvpn", "us texas"), Server("openvpn", "DE Berlin"), Server("openvpn", "US Texas")],
        });

        var regions = await GetRegionsAsync(Pia, "openvpn");

        Assert.AreSequenceEqual(BerlinAndLowerCaseTexas, regions.Regions);
    }

    [TestMethod]
    public async Task SkipsServersOfTheOtherVpnTypeAndServersWithoutARegion()
    {
        WriteServers(new()
        {
            [Pia] = [Server("openvpn", "US Texas"), Server("wireguard", "DE Berlin"), Server("openvpn", null), Server("openvpn", "")],
        });

        var regions = await GetRegionsAsync(Pia, "openvpn");

        Assert.AreSequenceEqual(Texas, regions.Regions);
    }

    [TestMethod]
    public async Task ListsOnlyTheServerRegionsInTheListsSpelling()
    {
        WriteServers(new()
        {
            [Pia] = [Server("openvpn", "US Texas"), Server("openvpn", "DE Berlin"), Server("openvpn", "Iceland")],
        });

        var regions = await GetRegionsAsync(CreateCatalog(serverRegions: ["de berlin", "US Texas", "Atlantis"]), Pia, "openvpn");

        Assert.AreSequenceEqual(BerlinAndTexas, regions.Regions);
    }

    [TestMethod]
    public async Task HasNoRegionsBeforeGluetunWritesItsServersList()
    {
        var regions = await GetRegionsAsync(Pia, "openvpn");

        Assert.IsEmpty(regions.Regions);
    }

    [TestMethod]
    public async Task HasNoRegionsForAProviderTheListDoesNotHave()
    {
        WriteServers(new() { ["nordvpn"] = [Server("openvpn", "Iceland")] });

        var regions = await GetRegionsAsync(Pia, "openvpn");

        Assert.IsEmpty(regions.Regions);
    }

    [TestMethod]
    public async Task HasNoRegionsWhileTheListIsHalfWritten()
    {
        // gluetun rewrites the file in place, so a read can catch it incomplete
        File.WriteAllText(ServersFile, $$"""{"version": 1, "{{Pia}}": {"servers": [{"vpn": "openvpn", "reg""");

        var regions = await GetRegionsAsync(Pia, "openvpn");

        Assert.IsEmpty(regions.Regions);
    }

    [TestMethod]
    public async Task RereadsTheListAfterGluetunRewritesIt()
    {
        var catalog = CreateCatalog();
        WriteServers(new() { [Pia] = [Server("openvpn", "US Texas")] });
        File.SetLastWriteTimeUtc(ServersFile, DateTime.UtcNow.AddMinutes(-1));
        await GetRegionsAsync(catalog, Pia, "openvpn");

        WriteServers(new() { [Pia] = [Server("openvpn", "DE Berlin")] });
        File.SetLastWriteTimeUtc(ServersFile, DateTime.UtcNow);
        var regions = await GetRegionsAsync(catalog, Pia, "openvpn");

        Assert.AreSequenceEqual(Berlin, regions.Regions);
    }

    [TestMethod]
    public async Task ReadsTheNewProvidersRegionsWhenGluetunSwitchesProvider()
    {
        var catalog = CreateCatalog();
        WriteServers(new()
        {
            [Pia] = [Server("openvpn", "US Texas")],
            ["nordvpn"] = [Server("openvpn", "Iceland")],
        });
        await GetRegionsAsync(catalog, Pia, "openvpn");

        var regions = await GetRegionsAsync(catalog, "nordvpn", "openvpn");

        Assert.AreEqual("nordvpn", regions.Provider);
        Assert.AreSequenceEqual(Iceland, regions.Regions);
    }

    private static JsonObject Server(string vpn, string? region)
    {
        var server = new JsonObject { ["vpn"] = vpn, ["server_name"] = "server", ["ips"] = new JsonArray("10.0.0.1") };
        if (region is not null)
        {
            server["region"] = region;
        }
        return server;
    }

    /// <summary>
    /// A servers list shaped like gluetun's: a version, then each provider's servers under its name.
    /// </summary>
    private void WriteServers(Dictionary<string, JsonObject[]> serversByProvider)
    {
        var file = new JsonObject { ["version"] = 1 };
        foreach (var (provider, servers) in serversByProvider)
        {
            file[provider] = new JsonObject
            {
                ["version"] = 1,
                ["timestamp"] = 1794155868,
                ["servers"] = new JsonArray([.. servers]),
            };
        }
        File.WriteAllText(ServersFile, file.ToJsonString());
    }

    private VpnRegionCatalog CreateCatalog(string[]? serverRegions = null) =>
        new(new VpnConfiguration(
                new Uri("http://gluetun.test/"), "key", _dataDirectory, new Uri("http://lookout@gluetun.test:8888/"), serverRegions),
            NullLogger<VpnRegionCatalog>.Instance);

    private Task<VpnRegions> GetRegionsAsync(string provider, string vpnType) =>
        GetRegionsAsync(CreateCatalog(), provider, vpnType);

    private async Task<VpnRegions> GetRegionsAsync(VpnRegionCatalog catalog, string provider, string vpnType)
    {
        using var httpClient = new HttpClient(new SettingsHandler(provider, vpnType)) { BaseAddress = new Uri("http://gluetun.test/") };
        return await catalog.GetRegionsAsync(new GluetunHttpClient(httpClient), TestContext.CancellationToken);
    }

    /// <summary>
    /// Answers <c>GET /v1/vpn/settings</c> as gluetun does, with the provider and VPN type it's using.
    /// </summary>
    private sealed class SettingsHandler(string provider, string vpnType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("/v1/vpn/settings", request.RequestUri!.AbsolutePath);
            var settings = JsonSerializer.Serialize(new
            {
                type = vpnType,
                provider = new { name = provider, server_selection = new { regions = (string[]?)null } },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(settings, Encoding.UTF8, "application/json"),
            });
        }
    }
}
