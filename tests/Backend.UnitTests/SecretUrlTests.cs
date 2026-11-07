using ChasePie.Lookout.Core;
using Microsoft.Extensions.Configuration;

namespace ChasePie.Lookout.Backend.UnitTests;

/// <summary>
/// The URLs whose secret comes from a setting of its own, so it can be read from a secret file.
/// </summary>
[TestClass]
public sealed class SecretUrlTests
{
    private static IConfiguration Configuration(params (string Name, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Name, s.Value)))
            .Build();

    [TestMethod]
    public void TheProxyPasswordIsAddedToTheProxyUrl()
    {
        var url = LookoutEnvironmentVariables.GetVpnProxyUrl(Configuration(
            (LookoutEnvironmentVariables.VpnProxyUrl, "http://lookout@gluetun:8888"),
            (LookoutEnvironmentVariables.VpnProxyPassword, "pa:ss/w@rd")));

        Assert.AreEqual("http://lookout:pa%3Ass%2Fw%40rd@gluetun:8888/", url?.AbsoluteUri);
    }

    [TestMethod]
    public void AProxyUrlWithoutAPasswordSettingIsKeptAsItIs()
    {
        var url = LookoutEnvironmentVariables.GetVpnProxyUrl(Configuration(
            (LookoutEnvironmentVariables.VpnProxyUrl, "http://lookout:secret@gluetun:8888")));

        Assert.AreEqual("http://lookout:secret@gluetun:8888/", url?.AbsoluteUri);
    }

    [TestMethod]
    public void WithoutAProxyUrlThereIsNoProxy()
    {
        var url = LookoutEnvironmentVariables.GetVpnProxyUrl(Configuration(
            (LookoutEnvironmentVariables.VpnProxyPassword, "secret")));

        Assert.IsNull(url);
    }

    [TestMethod]
    [DataRow("http://browserless:3000", "http://browserless:3000/?token=t0k%2Ben")]
    [DataRow("http://browserless:3000/?timeout=1000", "http://browserless:3000/?timeout=1000&token=t0k%2Ben")]
    public void TheBrowserlessTokenIsAddedToItsUrl(string browserlessUrl, string expected)
    {
        var url = LookoutEnvironmentVariables.GetBrowserlessUrl(Configuration(
            (LookoutEnvironmentVariables.BrowserlessUrl, browserlessUrl),
            (LookoutEnvironmentVariables.BrowserlessToken, "t0k+en")));

        Assert.AreEqual(expected, url);
    }

    [TestMethod]
    public void ABrowserlessUrlWithoutATokenSettingIsKeptAsItIs()
    {
        var url = LookoutEnvironmentVariables.GetBrowserlessUrl(Configuration(
            (LookoutEnvironmentVariables.BrowserlessUrl, "http://browserless:3000?token=abc")));

        Assert.AreEqual("http://browserless:3000?token=abc", url);
    }
}
