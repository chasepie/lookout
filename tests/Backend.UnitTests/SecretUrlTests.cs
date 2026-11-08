using ChasePie.Lookout.Core;
using Microsoft.Extensions.Configuration;

namespace ChasePie.Lookout.Backend.UnitTests;

/// <summary>
/// The URLs whose secret comes from a setting of its own.
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
        var url = EnvironmentVariables.GetVpnProxyUrl(Configuration(
            (EnvironmentVariables.GluetunProxyUrl, "http://lookout@gluetun:8888"),
            (EnvironmentVariables.GluetunProxyPassword, "pa:ss/w@rd")));

        Assert.AreEqual("http://lookout:pa%3Ass%2Fw%40rd@gluetun:8888/", url?.AbsoluteUri);
    }

    [TestMethod]
    public void AProxyUrlWithoutAPasswordSettingIsKeptAsItIs()
    {
        var url = EnvironmentVariables.GetVpnProxyUrl(Configuration(
            (EnvironmentVariables.GluetunProxyUrl, "http://lookout:secret@gluetun:8888")));

        Assert.AreEqual("http://lookout:secret@gluetun:8888/", url?.AbsoluteUri);
    }

    [TestMethod]
    [DataRow("http://lookout@gluetun:8888")]
    [DataRow("http://gluetun:8888")]
    public void AProxyUrlWithoutAPasswordIsRefused(string proxyUrl)
    {
        // gluetun's proxy without a password lets anyone who reaches it out through the VPN
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => EnvironmentVariables.GetVpnProxyUrl(
            Configuration((EnvironmentVariables.GluetunProxyUrl, proxyUrl))));

        Assert.Contains(EnvironmentVariables.GluetunProxyPassword, error.Message);
    }

    [TestMethod]
    public void WithoutAProxyUrlThereIsNoProxy()
    {
        var url = EnvironmentVariables.GetVpnProxyUrl(Configuration(
            (EnvironmentVariables.GluetunProxyPassword, "secret")));

        Assert.IsNull(url);
    }

    [TestMethod]
    public void TheBrowserlessTokenIsAddedToItsUrl()
    {
        var url = EnvironmentVariables.GetBrowserlessUrl(Configuration(
            (EnvironmentVariables.BrowserlessUrl, "http://browserless:3000"),
            (EnvironmentVariables.BrowserlessToken, "t0k+en")));

        Assert.AreEqual("http://browserless:3000/?token=t0k%2Ben", url);
    }

    [TestMethod]
    public void ABrowserlessUrlWithoutATokenIsRefused()
    {
        // browserless accepts anyone without one
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => EnvironmentVariables.GetBrowserlessUrl(
            Configuration((EnvironmentVariables.BrowserlessUrl, "http://browserless:3000"))));

        Assert.Contains(EnvironmentVariables.BrowserlessToken, error.Message);
    }
}
