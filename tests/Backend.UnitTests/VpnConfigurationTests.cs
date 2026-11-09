using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.Extensions.Configuration;

namespace ChasePie.Lookout.Backend.UnitTests;

[TestClass]
public sealed class VpnConfigurationTests
{
    private static IConfiguration Configuration(string? apiKey) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EnvVars.GluetunUrl] = "http://gluetun-control:8000",
                [EnvVars.GluetunApiKey] = apiKey,
                [EnvVars.GluetunProxyUrl] = "http://lookout@gluetun-proxy:8888",
                [EnvVars.GluetunProxyPassword] = "secret",
                [EnvVars.GluetunDataDir] = Path.Join(Path.GetTempPath(), "lookout-vpn-configuration-tests"),
            })
            .Build();

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
