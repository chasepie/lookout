using System.Net;
using System.Net.Http.Json;
using ChasePie.Lookout.Backend.Auth;

namespace Backend.IntegrationTests;

/// <summary>
/// That <see cref="CsrfProtection"/> guards the Backend. Its rules are covered by the unit tests.
/// </summary>
[TestClass]
public sealed class CsrfProtectionTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task ASignedInRequestFromAnotherSiteIsRejected()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        // A sibling subdomain is the same site but another origin
        client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Watches/1/runs");
        request.Headers.Add("Sec-Fetch-Site", "same-site");

        using var response = await client.SendAsync(request, Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task SigningInNeedsTheHeadersToo()
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        using var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { LookoutFactory.Username, LookoutFactory.Password }, Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Contains(CsrfProtection.HeaderName, await login.Content.ReadAsStringAsync(Ct));
    }
}
