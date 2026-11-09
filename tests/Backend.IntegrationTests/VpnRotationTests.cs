using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// The rotation service's checks when the Backend starts, against a fake gluetun whose tunnel comes up at once.
/// </summary>
[TestClass]
public sealed class VpnRotationTests
{
    private const string Pia = "private internet access";

    // Set before the Backend starts, and replaced by the outcome of the switch it makes, which tells the tests it's done
    private const string StaleError = "An error from before the restart.";

    private static readonly string[] Berlin = ["DE Berlin"];

    private static readonly DateTime LastSwitchTime = DateTime.UtcNow.AddDays(-1);
    private static readonly DateTime NextRotationTime = DateTime.UtcNow.AddDays(1);

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task ReplacesARegionTheServerRegionsNoLongerAllowEvenIfEveryAllowedOneIsExcluded()
    {
        // Exclusions only apply to rotation; honouring them here would leave the VPN on Iceland for good
        var (settings, gluetun) = await StartWithStoredRegionAsync(
            "Iceland", gluetunRegions: ["Iceland"], excludedRegions: Berlin);

        Assert.IsNull(settings.LastError);
        Assert.AreEqual("DE Berlin", settings.CurrentRegion);
        Assert.AreSequenceEqual(Berlin, gluetun.SwitchedTo.ToArray());
    }

    [TestMethod]
    public async Task ReplacingARegionCountsAsASwitch()
    {
        // The VPN changed region, so rotating right after would restart the tunnel twice in a row
        var (settings, _) = await StartWithStoredRegionAsync("Iceland", gluetunRegions: ["Iceland"]);

        Assert.IsNull(settings.LastError);
        Assert.IsGreaterThan(LastSwitchTime, settings.LastSwitchTime!.Value);
        Assert.IsLessThan(NextRotationTime, settings.NextRotationTime!.Value);
    }

    [TestMethod]
    public async Task ReapplyingALostRegionKeepsTheRotationSchedule()
    {
        // gluetun restarted and forgot the region: it may pick a server in any region
        var (settings, gluetun) = await StartWithStoredRegionAsync("DE Berlin", gluetunRegions: null);

        Assert.IsNull(settings.LastError);
        Assert.AreEqual("DE Berlin", settings.CurrentRegion);
        Assert.AreSequenceEqual(Berlin, gluetun.SwitchedTo.ToArray());
        Assert.AreEqual(LastSwitchTime, settings.LastSwitchTime);
        Assert.AreEqual(NextRotationTime, settings.NextRotationTime);
    }

    [TestMethod]
    public async Task RotationWithOnlyOneAllowedRegionBlamesTheServerRegions()
    {
        // Nothing is excluded, so blaming the exclusions would send the Owner to the wrong setting
        var (settings, gluetun) = await StartWithStoredRegionAsync(
            "DE Berlin", gluetunRegions: Berlin, rotationDue: true);

        Assert.Contains(EnvVars.GluetunServerRegions, settings.LastError!);
        Assert.DoesNotContain("excluded", settings.LastError!);
        Assert.IsEmpty(gluetun.SwitchedTo);
    }

    [TestMethod]
    public async Task SwitchingWhenTheServerRegionsMatchNoRegionBlamesThem()
    {
        // A typo, or a region the provider dropped: the servers list itself is fine
        var (settings, gluetun) = await StartWithStoredRegionAsync(
            null, gluetunRegions: null, serverRegions: "US Texass", rotationDue: true);

        Assert.Contains(EnvVars.GluetunServerRegions, settings.LastError!);
        Assert.IsEmpty(gluetun.SwitchedTo);
    }

    /// <summary>
    /// Stores <paramref name="currentRegion"/> as a Backend without the VPN would have, then starts one limited to
    /// <paramref name="serverRegions"/>, and returns the VPN settings once it has tried to switch.
    /// </summary>
    /// <param name="gluetunRegions">The regions gluetun is restricted to when the Backend starts.</param>
    /// <param name="rotationDue">Whether a rotation is due when the Backend starts, rather than in a day.</param>
    private async Task<(VpnSettings Settings, FakeGluetun Gluetun)> StartWithStoredRegionAsync(
        string? currentRegion,
        string[]? gluetunRegions,
        string[]? excludedRegions = null,
        string serverRegions = "DE Berlin",
        bool rotationDue = false)
    {
        await using var before = new LookoutFactory();
        await UpdateVpnSettingsAsync(before, settings =>
        {
            settings.RotationEnabled = true;
            settings.ExcludedRegions = [.. excludedRegions ?? []];
            settings.CurrentRegion = currentRegion;
            settings.LastSwitchTime = LastSwitchTime;
            settings.NextRotationTime = rotationDue ? LastSwitchTime : NextRotationTime;
            settings.LastError = StaleError;
        });

        var gluetunDataDirectory = Path.Join(before.DataDirectory, "gluetun");
        WriteServers(gluetunDataDirectory, "DE Berlin", "Iceland", "US Texas");
        var gluetun = new FakeGluetun(gluetunRegions);
        await using var after = new LookoutFactory(
            dataDirectory: before.DataDirectory,
            settings: new Dictionary<string, string?>
            {
                [EnvVars.GluetunUrl] = "http://gluetun-control.test:8000",
                [EnvVars.GluetunApiKey] = "key",
                [EnvVars.GluetunProxyUrl] = "http://lookout@gluetun-proxy.test:8888",
                [EnvVars.GluetunProxyPassword] = "secret",
                [EnvVars.GluetunDataDir] = gluetunDataDirectory,
                [EnvVars.GluetunServerRegions] = serverRegions,
            },
            configureServices: services =>
            {
                services.AddHttpClient<GluetunHttpClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(gluetun.HandleAsync));
                // The tunnel is up as soon as gluetun switches
                services.AddHttpClient<VpnProbeHttpClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ =>
                        Task.FromResult(Text(HttpStatusCode.OK, "ip=203.0.113.7\n"))));
            });

        var settings = await WaitForSwitchAsync(after);
        return (settings, gluetun);
    }

    private async Task UpdateVpnSettingsAsync(LookoutFactory factory, Action<VpnSettings> update)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var settings = await dbContext.VpnSettings.SingleAsync(s => s.Id == VpnSettings.SingletonId, Ct);
        update(settings);
        await dbContext.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Starts the Backend and waits for its switch to record its outcome, which replaces <see cref="StaleError"/>.
    /// </summary>
    private async Task<VpnSettings> WaitForSwitchAsync(LookoutFactory factory)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
                var settings = await dbContext.VpnSettings.AsNoTracking()
                    .SingleAsync(s => s.Id == VpnSettings.SingletonId, Ct);
                if (settings.LastError != StaleError)
                {
                    return settings;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The Backend didn't switch the VPN within 30 seconds.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }
    }

    private static void WriteServers(string dataDirectory, params string[] regions)
    {
        Directory.CreateDirectory(dataDirectory);
        var servers = new JsonObject
        {
            [Pia] = new JsonObject
            {
                ["servers"] = new JsonArray([.. regions.Select(region => new JsonObject
                {
                    ["vpn"] = "openvpn",
                    ["region"] = region,
                })]),
            },
        };
        File.WriteAllText(Path.Join(dataDirectory, "servers.json"), servers.ToJsonString());
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "text/plain") };

    /// <summary>
    /// gluetun's control server, for the requests <see cref="GluetunHttpClient"/> makes.
    /// </summary>
    /// <param name="regions">The regions gluetun is restricted to; <see langword="null"/> for any region.</param>
    private sealed class FakeGluetun(string[]? regions)
    {
        private volatile string[]? _regions = regions;

        /// <summary>
        /// The regions gluetun was switched to, in order.
        /// </summary>
        public ConcurrentQueue<string> SwitchedTo { get; } = new();

        public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
        {
            if (request.RequestUri?.AbsolutePath != "/v1/vpn/settings")
            {
                return Text(HttpStatusCode.NotFound, "not found");
            }

            if (request.Method == HttpMethod.Put)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
                var region = body!["provider"]!["server_selection"]!["regions"]![0]!.GetValue<string>();
                SwitchedTo.Enqueue(region);
                _regions = [region];
                return Text(HttpStatusCode.OK, "settings updated");
            }

            var settings = new JsonObject
            {
                ["type"] = "openvpn",
                ["provider"] = new JsonObject
                {
                    ["name"] = Pia,
                    ["server_selection"] = new JsonObject
                    {
                        ["regions"] = _regions is { } current ? new JsonArray([.. current.Select(r => JsonValue.Create(r))]) : null,
                    },
                },
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}
