using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed partial class AccountTests(TestContext testContext)
{
    [TestMethod]
    public async Task SetupCreatesTheAccountAndSignsInOnlyOnce()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateAppClientAsync(testContext.CancellationToken);
        Assert.IsFalse((await SessionStatus(client))["accountExists"]!.GetValue<bool>());

        var setup = await client.PostAsJsonAsync("/api/setup", new { userName = SignedInClients.UserName, password = SignedInClients.Password }, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Created, setup.StatusCode);
        var session = await SessionStatus(client);
        Assert.IsTrue(session["accountExists"]!.GetValue<bool>());
        Assert.AreEqual("owner", session["userName"]!.GetValue<string>());
        using var other = await factory.CreateAppClientAsync(testContext.CancellationToken);
        var again = await other.PostAsJsonAsync("/api/setup", new { userName = "intruder", password = "another password" }, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        Assert.IsNull((await SessionStatus(other))["userName"]);
        var login = await other.PostAsJsonAsync("/api/session", new { userName = "intruder", password = "another password" }, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [TestMethod]
    [DataRow("owner", "wrong password")]
    [DataRow("someone", "correct horse")]
    [DataRow("", "")]
    public async Task RejectsLoginWithWrongCredentials(string userName, string password)
    {
        await using var factory = new LookoutWebApplicationFactory();
        using (await factory.CreateSignedInClientAsync(testContext.CancellationToken)) { }
        using var client = await factory.CreateAppClientAsync(testContext.CancellationToken);

        var login = await client.PostAsJsonAsync("/api/session", new { userName, password }, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.IsNull((await SessionStatus(client))["userName"]);
        var watches = await client.GetAsync(new Uri("/api/watches", UriKind.Relative), testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, watches.StatusCode);
    }

    [TestMethod]
    public async Task LogsInWithTheAccountsCredentials()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using (await factory.CreateSignedInClientAsync(testContext.CancellationToken)) { }
        using var client = await factory.CreateAppClientAsync(testContext.CancellationToken);

        var login = await client.PostAsJsonAsync("/api/session", new { userName = " Owner ", password = "correct horse" }, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        Assert.AreEqual("owner", (await SessionStatus(client))["userName"]!.GetValue<string>());
        var watches = await client.GetAsync(new Uri("/api/watches", UriKind.Relative), testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, watches.StatusCode);
    }

    [TestMethod]
    public async Task LogoutEndsTheSessionEvenForACopyOfItsCookie()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateAppClientAsync(testContext.CancellationToken);
        var setup = await client.PostAsJsonAsync("/api/setup", new { userName = SignedInClients.UserName, password = SignedInClients.Password }, testContext.CancellationToken);
        var cookie = SessionCookie(setup).Split(';')[0];
        using var copy = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        Assert.AreEqual(HttpStatusCode.OK, (await SendWithCookie(copy, HttpMethod.Get, "/api/watches", cookie)).StatusCode);
        await client.RefreshRequestTokenAsync(testContext.CancellationToken);

        var logout = await client.DeleteAsync(new Uri("/api/session", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("expires=Thu, 01 Jan 1970", SessionCookie(logout));
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await SendWithCookie(copy, HttpMethod.Get, "/api/watches", cookie)).StatusCode);
    }

    [TestMethod]
    public async Task IssuesAnHttpOnlySameSiteCookieThatLastsUntilLogout()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateAppClientAsync(testContext.CancellationToken);

        var setup = await client.PostAsJsonAsync("/api/setup", new { userName = SignedInClients.UserName, password = SignedInClients.Password }, testContext.CancellationToken);

        var cookie = SessionCookie(setup);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("expires=", cookie);
    }

    [TestMethod]
    public async Task ProtectsEveryApiEndpointExceptSetupAndLogin()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();
        string[] anonymous = ["GET api/session", "POST api/session", "POST api/setup"];
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.TrimStart('/').StartsWith("api/", StringComparison.Ordinal) == true)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText!.TrimStart('/')}"))
            .ToList();
        Assert.IsGreaterThan(anonymous.Length, endpoints.Count);
        CollectionAssert.IsSubsetOf(anonymous, endpoints);

        foreach (var endpoint in endpoints.Except(anonymous))
        {
            var (method, pattern) = (endpoint.Split(' ')[0], endpoint.Split(' ')[1]);
            var path = "/" + RouteParameter().Replace(pattern, Guid.NewGuid().ToString());
            using var request = new HttpRequestMessage(new HttpMethod(method), path);

            var response = await client.SendAsync(request, testContext.CancellationToken);

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, endpoint);
        }
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();

    private async Task<HttpResponseMessage> SendWithCookie(HttpClient client, HttpMethod method, string path, string cookie)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request, testContext.CancellationToken);
    }

    private static string SessionCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("Lookout.Session=", StringComparison.Ordinal));

    private async Task<JsonObject> SessionStatus(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonObject>("/api/session", testContext.CancellationToken))!;
}
