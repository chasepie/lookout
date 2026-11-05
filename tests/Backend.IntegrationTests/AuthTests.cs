using System.Net;
using System.Net.Http.Json;
using ChasePie.Lookout.Backend.Auth;
using ChasePie.Lookout.Backend.Controllers;

namespace Backend.IntegrationTests;

[TestClass]
public sealed class AuthTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task SigningInStartsASession()
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        using var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { LookoutFactory.Username, LookoutFactory.Password }, Ct);

        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        var me = await client.GetFromJsonAsync<OwnerInfo>("/api/auth/me", Ct);
        Assert.AreEqual(LookoutFactory.Username, me?.Username);
        using var watches = await client.GetAsync("/api/Watches", Ct);
        Assert.AreEqual(HttpStatusCode.OK, watches.StatusCode);
    }

    [TestMethod]
    public async Task TheUsernameIgnoresCase()
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        using var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { Username = "OWNER", LookoutFactory.Password }, Ct);

        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
    }

    [TestMethod]
    [DataRow(LookoutFactory.Username, "wrong password!")]
    [DataRow("someone", LookoutFactory.Password)]
    [DataRow(LookoutFactory.Username, "CORRECT HORSE BATTERY")]
    public async Task WrongCredentialsAreRejectedWithTheSameMessage(string username, string password)
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        using var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password }, Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.AreEqual("Username or password is incorrect.", await login.Content.ReadAsStringAsync(Ct));
        Assert.IsFalse(login.Headers.Contains("Set-Cookie"));
        using var me = await client.GetAsync("/api/auth/me", Ct);
        Assert.AreEqual(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""{ "username": "", "password": "" }""")]
    [DataRow("""{ "username": "owner" }""")]
    public async Task MissingCredentialsGetTheSameMessageAsWrongOnes(string body)
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        using var login = await client.PostAsync(
            "/api/auth/login", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.AreEqual("Username or password is incorrect.", await login.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task TheSessionCookieIsHttpOnlySecureAndStrict()
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        using var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { LookoutFactory.Username, LookoutFactory.Password }, Ct);

        var cookie = login.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(OwnerAuthentication.CookieName + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        // Persistent, so it outlives the browser session
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task SigningOutEndsTheSession()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);

        using var logout = await client.PostAsync("/api/auth/logout", null, Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        using var me = await client.GetAsync("/api/auth/me", Ct);
        Assert.AreEqual(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [TestMethod]
    public async Task SessionsSurviveARestartWithTheSameCredentials()
    {
        await using var before = new LookoutFactory();
        var cookie = await SignInAndGetCookieAsync(before);

        await using var after = new LookoutFactory(dataDirectory: before.DataDirectory);
        using var me = await GetMeWithCookieAsync(after, cookie);

        Assert.AreEqual(HttpStatusCode.OK, me.StatusCode);
    }

    [TestMethod]
    [DataRow(LookoutFactory.Username, "a different password")]
    [DataRow("someone-else", LookoutFactory.Password)]
    public async Task ChangingTheCredentialsSignsOutEverySession(string newUsername, string newPassword)
    {
        await using var before = new LookoutFactory();
        var cookie = await SignInAndGetCookieAsync(before);

        await using var after = new LookoutFactory(
            username: newUsername, password: newPassword, dataDirectory: before.DataDirectory);
        using var me = await GetMeWithCookieAsync(after, cookie);

        Assert.AreEqual(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [TestMethod]
    public async Task SignInsAreLimitedPerClient()
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        for (var i = 0; i < OwnerAuthentication.SignInsPerClient; i++)
        {
            using var allowed = await SignInFromAsync(client, "10.0.0.1", "wrong password!");
            Assert.AreEqual(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        // Even the right password waits once the limit is reached
        using var limited = await SignInFromAsync(client, "10.0.0.1", LookoutFactory.Password);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.IsNotNull(limited.Headers.RetryAfter);

        using var otherClient = await SignInFromAsync(client, "10.0.0.2", LookoutFactory.Password);
        Assert.AreEqual(HttpStatusCode.OK, otherClient.StatusCode);
    }

    [TestMethod]
    public async Task SignInsAreLimitedInTotal()
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        for (var i = 0; i < OwnerAuthentication.SignInsInTotal; i++)
        {
            // A new IP each time, so only the total limit applies
            using var allowed = await SignInFromAsync(client, $"10.0.1.{i}", "wrong password!");
            Assert.AreEqual(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using var limited = await SignInFromAsync(client, "10.0.2.1", LookoutFactory.Password);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [TestMethod]
    public async Task OtherRequestsAreNotRateLimited()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);

        for (var i = 0; i < OwnerAuthentication.SignInsInTotal + 1; i++)
        {
            using var response = await client.GetAsync("/api/auth/me", Ct);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> SignInFromAsync(HttpClient client, string ip, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { LookoutFactory.Username, Password = password }),
        };
        request.Headers.Add(LookoutFactory.ClientIpHeader, ip);
        return await client.SendAsync(request, Ct);
    }

    private async Task<string> SignInAndGetCookieAsync(LookoutFactory factory)
    {
        using var client = factory.CreateBrowserClient();
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { LookoutFactory.Username, LookoutFactory.Password }, Ct);
        login.EnsureSuccessStatusCode();
        // Just "name=value", as a browser would send it back
        return login.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(OwnerAuthentication.CookieName + "=", StringComparison.Ordinal))
            .Split(';')[0];
    }

    private async Task<HttpResponseMessage> GetMeWithCookieAsync(LookoutFactory factory, string cookie)
    {
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request, Ct);
    }
}
