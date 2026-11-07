using System.Net;
using System.Net.Http.Json;
using ChasePie.Lookout.Backend.Auth;
using ChasePie.Lookout.Backend.Controllers;

namespace ChasePie.Lookout.Backend.IntegrationTests;

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
    [DataRow("")]
    [DataRow("null")]
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
        // What the __Host- prefix requires, or browsers reject the cookie
        Assert.StartsWith("__Host-", cookie);
        Assert.Contains("path=/;", cookie + ";", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
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
    public async Task SigningOutEndsEverySessionIncludingCopiesOfTheCookie()
    {
        await using var factory = new LookoutFactory();
        var cookie = await SignInAndGetCookieAsync(factory);
        var otherSession = await SignInAndGetCookieAsync(factory);

        using var logout = await SendWithCookieAsync(factory, HttpMethod.Post, "/api/auth/logout", cookie);

        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        // The cookie signing out sent, as a stolen copy would still hold it
        using var copy = await GetMeWithCookieAsync(factory, cookie);
        Assert.AreEqual(HttpStatusCode.Unauthorized, copy.StatusCode);
        using var other = await GetMeWithCookieAsync(factory, otherSession);
        Assert.AreEqual(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [TestMethod]
    public async Task SigningOutWithoutASessionOnlyClearsTheCookie()
    {
        await using var factory = new LookoutFactory();
        var session = await SignInAndGetCookieAsync(factory);
        using var anonymous = factory.CreateBrowserClient();

        using var logout = await anonymous.PostAsync("/api/auth/logout", null, Ct);

        // Anyone can call it, so it mustn't sign the Owner out
        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        using var me = await GetMeWithCookieAsync(factory, session);
        Assert.AreEqual(HttpStatusCode.OK, me.StatusCode);
    }

    [TestMethod]
    [DataRow("10.0.0.1", HttpStatusCode.BadRequest)]
    [DataRow("2001:db8::1", HttpStatusCode.BadRequest)]
    [DataRow("127.0.0.1", HttpStatusCode.OK)]
    [DataRow("::1", HttpStatusCode.OK)]
    public async Task SigningInOverPlainHttpOnlyWorksFromThisMachine(string ip, HttpStatusCode expected)
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient(new Uri("http://localhost"));

        using var login = await SignInFromAsync(client, ip, LookoutFactory.Password);

        Assert.AreEqual(expected, login.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
        {
            Assert.Contains("HTTPS", await login.Content.ReadAsStringAsync(Ct));
        }
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
    public async Task AnIpv6NetworkCountsAsOneClient()
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        for (var i = 1; i <= OwnerAuthentication.SignInsPerClient; i++)
        {
            // A new address in the same /64 each time
            using var allowed = await SignInFromAsync(client, $"2001:db8:0:1::{i}", "wrong password!");
            Assert.AreEqual(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using var limited = await SignInFromAsync(client, "2001:db8:0:1::ffff", LookoutFactory.Password);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        using var otherNetwork = await SignInFromAsync(client, "2001:db8:0:2::1", LookoutFactory.Password);
        Assert.AreEqual(HttpStatusCode.OK, otherNetwork.StatusCode);
    }

    [TestMethod]
    public async Task ManyClientsDoNotLockTheOwnerOut()
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        // An attacker using up each of several addresses' limits
        for (var network = 1; network <= 5; network++)
        {
            for (var i = 0; i < OwnerAuthentication.SignInsPerClient; i++)
            {
                using var attempt = await SignInFromAsync(client, $"10.0.{network}.1", "wrong password!");
                Assert.AreEqual(HttpStatusCode.Unauthorized, attempt.StatusCode);
            }
        }

        using var owner = await SignInFromAsync(client, "192.168.1.20", LookoutFactory.Password);
        Assert.AreEqual(HttpStatusCode.OK, owner.StatusCode);
    }

    [TestMethod]
    public async Task OtherRequestsAreNotRateLimited()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);

        for (var i = 0; i < OwnerAuthentication.SignInsPerClient + 1; i++)
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
        return LookoutFactory.SessionCookie(login);
    }

    private Task<HttpResponseMessage> GetMeWithCookieAsync(LookoutFactory factory, string cookie) =>
        SendWithCookieAsync(factory, HttpMethod.Get, "/api/auth/me", cookie);

    /// <summary>
    /// Sends a browser-like request carrying exactly the given cookie, as a client holding a copy of it would.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithCookieAsync(
        LookoutFactory factory, HttpMethod method, string path, string cookie)
    {
        using var client = factory.CreateBrowserClient(handleCookies: false);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request, Ct);
    }
}
