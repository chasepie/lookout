using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// A page on another port of the same host counts as the same site, so SameSite cookies alone don't stop it sending
/// requests with the user's session. Lookout also requires the request token only its own app can read.
/// </summary>
[TestClass]
public sealed class RequestForgeryTests(TestContext testContext)
{
    [TestMethod]
    public async Task IssuesARequestTokenTheAppCanReadWithTheSessionStatus()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/session", UriKind.Relative), testContext.CancellationToken);

        // Other Angular apps on the same host share its cookies, so the default XSRF-TOKEN name would collide.
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("Lookout.XSRF-TOKEN=", StringComparison.Ordinal));
        Assert.DoesNotContain("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
    }

    [TestMethod]
    public async Task RejectsSetupAndLoginWithoutTheRequestToken()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();
        var credentials = new { userName = SignedInClients.UserName, password = SignedInClients.Password };

        var setup = await client.PostAsJsonAsync("/api/setup", credentials, testContext.CancellationToken);
        var login = await client.PostAsJsonAsync("/api/session", credentials, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, setup.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, login.StatusCode);
        var status = await client.GetFromJsonAsync<JsonObject>("/api/session", testContext.CancellationToken);
        Assert.IsFalse(status!["accountExists"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task RejectsChangesFromALoggedInUserWithoutTheRequestToken()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        client.DefaultRequestHeaders.Remove(SignedInClients.RequestTokenHeader);
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Widget"), "name" },
            { new StringContent("https://example.com"), "notificationUrl" },
            { new StringContent("export default {}"), "script", "price.ts" }
        };

        var create = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
        var logout = await client.DeleteAsync(new Uri("/api/session", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, logout.StatusCode);
        // The app recognises this problem, unlike a validation error, and retries with a fresh token.
        var problem = await logout.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.AreEqual("urn:lookout:problem:request-token", problem!["type"]!.GetValue<string>());
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.IsEmpty(watches!);
    }

    [TestMethod]
    public async Task RejectsARequestTokenSentAsAFormField()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        // A page on another port can read the token cookie and post a form without a CORS preflight, but can't send the header.
        var token = client.DefaultRequestHeaders.GetValues(SignedInClients.RequestTokenHeader).Single();
        client.DefaultRequestHeaders.Remove(SignedInClients.RequestTokenHeader);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("Widget"), "name" },
            { new StringContent("https://example.com"), "notificationUrl" },
            { new StringContent("interval"), "scheduleKind" },
            { new StringContent("1"), "every" },
            { new StringContent("hours"), "unit" },
            { new StringContent("export default {}"), "script", "price.ts" }
        };

        var create = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, create.StatusCode);
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.IsEmpty(watches!);
    }

    [TestMethod]
    public async Task RejectsARequestTokenIssuedBeforeLogin()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateAppClientAsync(testContext.CancellationToken);
        var setup = await client.PostAsJsonAsync("/api/setup",
            new { userName = SignedInClients.UserName, password = SignedInClients.Password }, testContext.CancellationToken);
        setup.EnsureSuccessStatusCode();

        var stale = await client.DeleteAsync(new Uri("/api/session", UriKind.Relative), testContext.CancellationToken);
        await client.RefreshRequestTokenAsync(testContext.CancellationToken);
        var fresh = await client.DeleteAsync(new Uri("/api/session", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, fresh.StatusCode);
    }
}
