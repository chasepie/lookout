using System.Net;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Serving the built frontend: its page for every path outside <c>/api</c>, and caching that survives rebuilds.
/// </summary>
[TestClass]
public sealed class FrontendServingTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    [DataRow("/")]
    [DataRow("/watches/3")]
    [DataRow("/login")]
    public async Task PagesGetTheFrontendWithoutASessionAndAreAlwaysRevalidated(string path)
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync(path, Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(LookoutFactory.IndexHtml, await response.Content.ReadAsStringAsync(Ct));
        Assert.IsTrue(response.Headers.CacheControl?.NoCache, $"Cache-Control: {response.Headers.CacheControl}");
    }

    [TestMethod]
    [DataRow(LookoutFactory.BundleFileName)]
    [DataRow(LookoutFactory.UnderscoreBundleFileName)]
    public async Task HashedBundlesAreCachedForGood(string bundle)
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync("/" + bundle, Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString() ?? "");
    }

    [TestMethod]
    [DataRow("GET", "/api")]
    [DataRow("GET", "/api/")]
    [DataRow("GET", "/api/nothing-here")]
    [DataRow("GET", "/API/nothing-here")]
    public async Task UnknownApiRoutesAreNotFoundRatherThanThePage(string method, string path)
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    [DataRow("DELETE", "/api/Watches")]
    [DataRow("PATCH", "/api/Watches/1")]
    public async Task AnApiRouteCalledWithTheWrongMethodIsNotAllowed(string method, string path)
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Ct);

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [TestMethod]
    public async Task UnknownApiRoutesNeedASession()
    {
        await using var factory = new LookoutFactory();
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync("/api/nothing-here", Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
