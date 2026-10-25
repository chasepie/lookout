using System.Net.Http.Json;
using ChasePie.Lookout.Backend.Security;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// HTTPS comes from the user's own reverse proxy (ADR-0002), which reaches Lookout over plain HTTP and says how the
/// browser's request came in with <c>X-Forwarded-Proto</c>.
/// </summary>
[TestClass]
public sealed class ReverseProxyTests(TestContext testContext)
{
    [TestMethod]
    [DataRow("https", true)]
    [DataRow(null, false)]
    public async Task MarksCookiesSecureOnlyWhenTheRequestCameOverHttps(string? forwardedProto, bool secure)
    {
        await using var factory = new LookoutWebApplicationFactory();
        // The test sends the cookies back itself, since a client wouldn't send Secure cookies to this plain-HTTP host.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // The request token's cookies come with the session status, and the session's with setup.
        using var status = await SendAsync(client, new HttpRequestMessage(HttpMethod.Get, "/api/session"), forwardedProto);
        var cookies = status.Headers.GetValues("Set-Cookie").ToList();
        client.DefaultRequestHeaders.Add(SignedInClients.RequestTokenHeader, Value(cookies, RequestForgery.TokenCookieName));
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies.Select(cookie => cookie[..cookie.IndexOf(';', StringComparison.Ordinal)])));
        using var setup = await SendAsync(client, new HttpRequestMessage(HttpMethod.Post, "/api/setup")
        {
            Content = JsonContent.Create(new { userName = SignedInClients.UserName, password = SignedInClients.Password }),
        }, forwardedProto);
        setup.EnsureSuccessStatusCode();
        cookies.AddRange(setup.Headers.GetValues("Set-Cookie"));

        CollectionAssert.IsSubsetOf(
            new[] { "Lookout.Antiforgery", RequestForgery.TokenCookieName, "Lookout.Session" },
            cookies.Select(Name).ToList());
        foreach (var cookie in cookies)
        {
            Assert.AreEqual(secure, cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase), cookie);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, string? forwardedProto)
    {
        using (request)
        {
            if (forwardedProto is not null)
            {
                request.Headers.Add("X-Forwarded-Proto", forwardedProto);
            }
            return await client.SendAsync(request, testContext.CancellationToken);
        }
    }

    private static string Name(string setCookie) => setCookie[..setCookie.IndexOf('=', StringComparison.Ordinal)];

    private static string Value(IEnumerable<string> setCookies, string name)
    {
        var setCookie = setCookies.Single(value => Name(value) == name);
        return setCookie[(name.Length + 1)..setCookie.IndexOf(';', StringComparison.Ordinal)];
    }
}
