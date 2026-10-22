using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Hosting;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class VpnConfigurationTests(TestContext testContext)
{
    [TestMethod]
    public async Task WithoutAnApiKeyTheVpnIsDisabled()
    {
        await using var factory = new LookoutWebApplicationFactory { UseConfiguredVpn = true };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var status = await client.GetFromJsonAsync<JsonObject>("/api/vpn", testContext.CancellationToken);

        Assert.IsFalse(status!["enabled"]!.GetValue<bool>());
        Assert.IsFalse(status["connected"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task WithAnApiKeyVpnRunsGoThroughGluetun()
    {
        await using var gluetun = await FakeGluetun.StartAsync("control-key");
        await using var factory = new LookoutWebApplicationFactory { UseConfiguredVpn = true };
        await using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Vpn:ApiKey", "control-key");
            builder.UseSetting("Vpn:ControlUrl", gluetun.Address.ToString());
            builder.UseSetting("Vpn:ProxyUrl", "http://vpn-proxy:3128");
            builder.UseSetting("Vpn:ProxyUsername", "lookout");
            builder.UseSetting("Vpn:ProxyPassword", "secret");
        });
        RunnerRequest? executed = null;
        factory.Runner.OnExecute = (request, _) =>
        {
            executed = request;
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);

        var status = await client.GetFromJsonAsync<JsonObject>("/api/vpn", testContext.CancellationToken);
        var run = await RunVpnWatch(client);

        Assert.AreEqual(
            """{"enabled":true,"connected":true,"rotating":false,"region":"Florida","exitIp":"198.51.100.23","error":null}""",
            status!.ToJsonString());
        Assert.AreEqual(new ProxySettings("http://vpn-proxy:3128", "lookout", "secret"), executed!.Proxy);
        Assert.AreEqual("198.51.100.23", run["vpnExitIp"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("Vpn:ControlUrl", "gluetun:8000")]
    [DataRow("Vpn:ProxyUrl", "not a URL")]
    [DataRow("Vpn:ProxyUrl", "http://lookout:secret@gluetun:8888", DisplayName = "Credentials in the proxy URL, which Chromium won't use")]
    [DataRow("Vpn:ProxyUrl", "http://gluetun:8888/vpn", DisplayName = "A path in the proxy URL, which a proxy can't have")]
    [DataRow("Vpn:ApiKey", "ab\"c", DisplayName = "An API key that would break gluetun's TOML")]
    [DataRow("Vpn:ApiKey", "a b", DisplayName = "An API key with a space")]
    public async Task RefusesToStartWithInvalidVpnSettings(string key, string value)
    {
        await using var factory = new LookoutWebApplicationFactory { UseConfiguredVpn = true };
        await using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Vpn:ApiKey", "control-key");
            builder.UseSetting(key, value);
        });

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains(key, error.Message);
    }

    [TestMethod]
    public async Task RotatingWithoutAVpnFailsThroughItsTask()
    {
        var rotation = new DisabledVpnRotator().RotateAsync(testContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rotation);
    }

    private async Task<JsonObject> RunVpnWatch(HttpClient client)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Widget price"), "name" },
            { new StringContent("https://example.com/widget"), "notificationUrl" },
            { new StringContent("export default { async run() { return { success: true, content: '£20' }; } };"), "script", "price.ts" },
            { new StringContent("interval"), "scheduleKind" },
            { new StringContent("1"), "every" },
            { new StringContent("hours"), "unit" },
            { new StringContent("true"), "usesVpn" }
        };
        var created = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["id"]!.GetValue<string>();
        var response = await client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
    }
}
