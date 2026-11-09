using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.Extensions.Configuration;

namespace ChasePie.Lookout.Backend.UnitTests;

[TestClass]
public sealed class VpnConfigurationTests
{
    private static readonly string[] BerlinAndTexas = ["DE Berlin", "US Texas"];

    private static IConfiguration Configuration(string? apiKey, string? serverRegions = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EnvVars.GluetunUrl] = "http://gluetun-control:8000",
                [EnvVars.GluetunApiKey] = apiKey,
                [EnvVars.GluetunProxyUrl] = "http://lookout@gluetun-proxy:8888",
                [EnvVars.GluetunProxyPassword] = "secret",
                [EnvVars.GluetunDataDir] = Path.Join(Path.GetTempPath(), "lookout-vpn-configuration-tests"),
                [EnvVars.GluetunServerRegions] = serverRegions,
            })
            .Build();

    [TestMethod]
    public void ReadsTheServerRegionsAsACommaSeparatedList()
    {
        // gluetun trims the whole value, but not the regions in it
        var configuration = VpnConfiguration.FromConfiguration(Configuration("key", " DE Berlin,US Texas,de berlin "));

        Assert.AreSequenceEqual(BerlinAndTexas, configuration.ServerRegions);
    }

    [TestMethod]
    [DataRow("DE Berlin, US Texas")]
    [DataRow("DE Berlin ,US Texas")]
    [DataRow("DE Berlin,,US Texas")]
    [DataRow("DE Berlin,")]
    public void RejectsServerRegionsGluetunReadsDifferently(string serverRegions)
    {
        // gluetun splits on the commas alone, so it would look for " US Texas", match no server, and keep quiet about it
        // unless no region matched at all, when it wouldn't start
        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => VpnConfiguration.FromConfiguration(Configuration("key", serverRegions)));

        Assert.Contains(EnvVars.GluetunServerRegions, error.Message);
    }

    [TestMethod]
    public void AllowsOnlyTheServerRegionsIgnoringCase()
    {
        var configuration = VpnConfiguration.FromConfiguration(Configuration("key", "DE Berlin,US Texas"));

        Assert.IsTrue(configuration.AllowsRegion("us texas"));
        Assert.IsFalse(configuration.AllowsRegion("Iceland"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public void AllowsEveryRegionWithoutServerRegions(string? serverRegions)
    {
        var configuration = VpnConfiguration.FromConfiguration(Configuration("key", serverRegions));

        Assert.IsTrue(configuration.AllowsRegion("Iceland"));
    }

    [TestMethod]
    public void ReadsGluetunsKey()
    {
        var configuration = VpnConfiguration.FromConfiguration(Configuration("key"));

        Assert.AreEqual("key", configuration.ApiKey);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public void WithGluetunConfiguredItsKeyIsRequired(string? apiKey)
    {
        // gluetun's control server refuses every request without one, so the VPN would only look configured
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => VpnConfiguration.FromConfiguration(Configuration(apiKey)));

        Assert.Contains(EnvVars.GluetunApiKey, error.Message);
    }
}
