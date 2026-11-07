using System.Net;
using System.Net.Http.Json;
using ChasePie.Lookout.Backend.Auth;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Serving Lookout in production: behind a reverse proxy, on its own hostname, and with the headers browsers need.
/// See docs/security.md.
/// </summary>
[TestClass]
public sealed class HostingTests
{
    private const string ProxyIp = "172.30.0.1";

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task ATrustedProxyTerminatingTlsCanSignIn()
    {
        await using var factory = Factory(LookoutEnvironmentVariables.TrustedProxies, ProxyIp);
        // The proxy talks to the Backend over plain HTTP
        using var client = factory.CreateBrowserClient(new Uri("http://localhost"));

        using var login = await SignInThroughProxyAsync(client, clientIp: "10.0.0.1", LookoutFactory.Password);

        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("10.0.0.0/24")]
    public async Task ForwardedHeadersFromAnUntrustedSourceAreIgnored(string? trustedProxies)
    {
        await using var factory = Factory(LookoutEnvironmentVariables.TrustedProxies, trustedProxies);
        using var client = factory.CreateBrowserClient(new Uri("http://localhost"));

        // Claiming HTTPS, and even this machine, doesn't make plain HTTP from elsewhere acceptable
        using var login = await SignInThroughProxyAsync(client, clientIp: "127.0.0.1", LookoutFactory.Password);

        Assert.AreEqual(HttpStatusCode.BadRequest, login.StatusCode);
    }

    [TestMethod]
    public async Task EachClientBehindTheProxyHasItsOwnSignInLimit()
    {
        await using var factory = Factory(LookoutEnvironmentVariables.TrustedProxies, "172.30.0.0/24");
        using var client = factory.CreateBrowserClient(new Uri("http://localhost"));

        for (var i = 0; i < OwnerAuthentication.SignInsPerClient; i++)
        {
            using var attempt = await SignInThroughProxyAsync(client, clientIp: "10.0.0.1", "wrong password!");
            Assert.AreEqual(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        using var limited = await SignInThroughProxyAsync(client, clientIp: "10.0.0.1", LookoutFactory.Password);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        using var owner = await SignInThroughProxyAsync(client, clientIp: "10.0.0.2", LookoutFactory.Password);
        Assert.AreEqual(HttpStatusCode.OK, owner.StatusCode);
    }

    [TestMethod]
    public async Task AMistypedTrustedProxyStopsStartup()
    {
        await using var factory = Factory(LookoutEnvironmentVariables.TrustedProxies, "172.30.0.1, proxy.local");

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("proxy.local", error.Message);
    }

    [TestMethod]
    [DataRow("https://lookout.example.com", HttpStatusCode.OK)]
    [DataRow("https://localhost", HttpStatusCode.BadRequest)]
    [DataRow("https://other.example.com", HttpStatusCode.BadRequest)]
    public async Task OnlyTheConfiguredHostnameIsServed(string address, HttpStatusCode expected)
    {
        await using var factory = Factory("AllowedHosts", "lookout.example.com");
        using var client = factory.CreateBrowserClient(new Uri(address));

        using var response = await client.GetAsync("/", Ct);

        Assert.AreEqual(expected, response.StatusCode);
    }

    [TestMethod]
    public async Task WithoutAHostnameOnlyLocalhostIsServed()
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient(new Uri("https://lookout.example.com"));

        using var response = await client.GetAsync("/", Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public async Task AnEmptyHostnameStopsStartupRatherThanAllowingEveryHost(string allowedHosts)
    {
        await using var factory = Factory("AllowedHosts", allowedHosts);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("AllowedHosts", error.Message);
    }

    [TestMethod]
    public async Task BrowsersAreToldToUseHttpsForAYear()
    {
        await using var factory = Factory("AllowedHosts", "lookout.example.com");
        using var client = factory.CreateBrowserClient(new Uri("https://lookout.example.com"));

        using var response = await client.GetAsync("/", Ct);

        // Not for subdomains, which may be other apps still on plain HTTP
        Assert.AreEqual("max-age=31536000", string.Join(", ", response.Headers.GetValues("Strict-Transport-Security")));
    }

    [TestMethod]
    public async Task DevelopmentDoesNotPinHttps()
    {
        await using var factory = Factory("AllowedHosts", "lookout.example.com", environment: "Development");
        using var client = factory.CreateBrowserClient(new Uri("https://lookout.example.com"));

        using var response = await client.GetAsync("/", Ct);

        Assert.IsFalse(response.Headers.Contains("Strict-Transport-Security"));
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/" + LookoutFactory.BundleFileName)]
    [DataRow("/api/auth/me")]
    [DataRow("/api/nothing-here")]
    public async Task EveryResponseLimitsWhatBrowsersDoWithIt(string path)
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync(path, Ct);

        Assert.AreEqual(
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
            + "connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; "
            + "frame-ancestors 'none'",
            Header(response, "Content-Security-Policy"));
        Assert.AreEqual("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.AreEqual("no-referrer", Header(response, "Referrer-Policy"));
    }

    [TestMethod]
    public async Task TheApiReferenceInDevelopmentCanLoadItsScripts()
    {
        await using var factory = new LookoutFactory(environment: "Development");
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync("/api-docs/", Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(response.Headers.Contains("Content-Security-Policy"));
    }

    [TestMethod]
    // Just over the script limit gets the 64 KiB check's message; far over is cut off once the limit is read, rather
    // than read whole first. MVC reports that as a 400 too, since it happens while binding the form.
    [DataRow(65 * 1024, "between 1 byte and 64 KiB")]
    [DataRow(200 * 1024, "The max request body size is 131072 bytes")]
    public async Task OversizedScriptsAreRefusedWithoutReadingThemWhole(int scriptBytes, string expectedError)
    {
        // On Kestrel, since the in-memory test server doesn't limit request bodies. Over plain HTTP from this machine,
        // so the Secure session cookie is sent back by hand.
        await using var factory = new LookoutFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        var address = factory.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>()
            .Addresses.First();
        // By name, as the only host allowed
        var baseAddress = new UriBuilder(address) { Host = "localhost" }.Uri;
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = baseAddress };
        LookoutFactory.AddBrowserHeaders(client);
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { LookoutFactory.Username, LookoutFactory.Password }, Ct);
        login.EnsureSuccessStatusCode();
        var cookie = LookoutFactory.SessionCookie(login);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Watches")
        {
            Content = new MultipartFormDataContent
            {
                { new StringContent("Too big"), "Name" },
                { new ByteArrayContent(new byte[scriptBytes]), "script", "watch.ts" },
            },
        };
        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedError, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// A Backend with one setting changed, as if from the environment.
    /// </summary>
    private static LookoutFactory Factory(string name, string? value, string environment = "Production") =>
        new(environment, settings: new Dictionary<string, string?> { [name] = value });

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : "(none)";

    /// <summary>
    /// Signs in as if through a reverse proxy at <see cref="ProxyIp"/> that received the request over HTTPS from
    /// <paramref name="clientIp"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SignInThroughProxyAsync(HttpClient client, string clientIp, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { LookoutFactory.Username, Password = password }),
        };
        request.Headers.Add(LookoutFactory.ClientIpHeader, ProxyIp);
        request.Headers.Add("X-Forwarded-For", clientIp);
        request.Headers.Add("X-Forwarded-Proto", "https");
        return await client.SendAsync(request, Ct);
    }
}
