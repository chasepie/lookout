using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// After 5 failed logins from one client address within 5 minutes, Lookout refuses that address's logins, without
/// checking them, until the 5 minutes are up.
/// </summary>
[TestClass]
public sealed class LoginLimitTests(TestContext testContext)
{
    [TestMethod]
    public async Task RefusesLoginsWithoutCheckingThemAfterFiveFailures()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using (await factory.CreateSignedInClientAsync(testContext.CancellationToken)) { }
        using var client = await factory.CreateAppClientAsync(testContext.CancellationToken);
        // Wrong user names count the same as wrong passwords.
        await FailLoginsAsync(client, 3);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await LogInAsync(client, "someone", SignedInClients.Password)).StatusCode);
        factory.Time.Advance(TimeSpan.FromMinutes(1));
        await FailLoginsAsync(client, 1);

        var login = await LogInAsync(client, SignedInClients.UserName, SignedInClients.Password);

        Assert.AreEqual(HttpStatusCode.TooManyRequests, login.StatusCode);
        Assert.AreEqual(TimeSpan.FromMinutes(4), login.Headers.RetryAfter?.Delta);
        var problem = await login.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.AreEqual("Too many failed logins. Try again in 4 minutes.", problem!["detail"]!.GetValue<string>());
        Assert.IsNull((await SessionStatus(client))["userName"]);
    }

    [TestMethod]
    public async Task AcceptsLoginsAgainOnceTheWindowEnds()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using (await factory.CreateSignedInClientAsync(testContext.CancellationToken)) { }
        using var client = await factory.CreateAppClientAsync(testContext.CancellationToken);
        await FailLoginsAsync(client, 5);
        factory.Time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(30));
        var refused = await LogInAsync(client, SignedInClients.UserName, SignedInClients.Password);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.AreEqual(TimeSpan.FromSeconds(30), refused.Headers.RetryAfter?.Delta);
        Assert.AreEqual("Too many failed logins. Try again in 1 minute.",
            (await refused.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["detail"]!.GetValue<string>());
        factory.Time.Advance(TimeSpan.FromSeconds(30));

        var login = await LogInAsync(client, SignedInClients.UserName, SignedInClients.Password);

        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
    }

    [TestMethod]
    public async Task LimitsEachAddressSeparately()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using (await factory.CreateSignedInClientAsync(testContext.CancellationToken)) { }
        using var limited = await factory.CreateAppClientAsync(testContext.CancellationToken);
        await FailLoginsAsync(limited, 5);
        using var other = await factory.CreateAppClientAsync(testContext.CancellationToken);
        other.DefaultRequestHeaders.Add(ClientAddressStartupFilter.HeaderName, "192.168.1.11");

        var login = await LogInAsync(other, SignedInClients.UserName, SignedInClients.Password);

        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        Assert.AreEqual(HttpStatusCode.TooManyRequests,
            (await LogInAsync(limited, SignedInClients.UserName, SignedInClients.Password)).StatusCode);
    }

    [TestMethod]
    public async Task CountsIpv6ClientsByTheirNetwork()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using (await factory.CreateSignedInClientAsync(testContext.CancellationToken)) { }
        using var limited = await factory.CreateAppClientAsync(testContext.CancellationToken);
        limited.DefaultRequestHeaders.Add(ClientAddressStartupFilter.HeaderName, "fd00:1:2:3::10");
        await FailLoginsAsync(limited, 5);
        using var sameNetwork = await factory.CreateAppClientAsync(testContext.CancellationToken);
        sameNetwork.DefaultRequestHeaders.Add(ClientAddressStartupFilter.HeaderName, "fd00:1:2:3:abcd::1");

        var login = await LogInAsync(sameNetwork, SignedInClients.UserName, SignedInClients.Password);

        Assert.AreEqual(HttpStatusCode.TooManyRequests, login.StatusCode);
    }

    [TestMethod]
    public async Task StartsTheCountAgainAfterALoginSucceeds()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using (await factory.CreateSignedInClientAsync(testContext.CancellationToken)) { }
        using var client = await factory.CreateAppClientAsync(testContext.CancellationToken);
        await FailLoginsAsync(client, 4);
        Assert.AreEqual(HttpStatusCode.NoContent,
            (await LogInAsync(client, SignedInClients.UserName, SignedInClients.Password)).StatusCode);
        await client.RefreshRequestTokenAsync(testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent,
            (await client.DeleteAsync(new Uri("/api/session", UriKind.Relative), testContext.CancellationToken)).StatusCode);
        await client.RefreshRequestTokenAsync(testContext.CancellationToken);

        await FailLoginsAsync(client, 4);
        var login = await LogInAsync(client, SignedInClients.UserName, SignedInClients.Password);

        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
    }

    private async Task FailLoginsAsync(HttpClient client, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var login = await LogInAsync(client, SignedInClients.UserName, "wrong password");
            Assert.AreEqual(HttpStatusCode.Unauthorized, login.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> LogInAsync(HttpClient client, string userName, string password) =>
        await client.PostAsJsonAsync("/api/session", new { userName, password }, testContext.CancellationToken);

    private async Task<JsonObject> SessionStatus(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonObject>("/api/session", testContext.CancellationToken))!;
}
