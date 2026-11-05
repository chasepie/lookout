using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.IntegrationTests;

/// <summary>
/// That every endpoint needs a session except the few meant to be open, so a new endpoint can't be left open by
/// mistake. Adding an anonymous endpoint means adding it here on purpose.
/// </summary>
[TestClass]
public sealed partial class EndpointProtectionTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    // Route pattern and method of each endpoint that works without a session, in every environment
    private static readonly HashSet<string> AnonymousEverywhere =
    [
        "POST api/auth/login",
        "POST api/auth/logout",
        // The built frontend's page, when wwwroot has one
        "GET {*path:nonfile}",
        "HEAD {*path:nonfile}",
    ];

    // Development only: the OpenAPI document and the health checks the AppHost polls
    private static readonly HashSet<string> AnonymousInDevelopment =
    [
        "GET /openapi/{documentName}.json",
        "* /health",
        "* /alive",
    ];

    // Development only: Scalar's page and the files it loads
    private const string ScalarPrefix = "GET /api-docs/";

    private static bool IsDevelopmentOnly(string key) =>
        AnonymousInDevelopment.Contains(key) || key.StartsWith(ScalarPrefix, StringComparison.Ordinal);

    [TestMethod]
    [DataRow("Production")]
    [DataRow("Development")]
    public async Task EveryEndpointNeedsASessionUnlessItIsMeantToBeOpen(string environment)
    {
        await using var factory = new LookoutFactory(environment);
        using var client = factory.CreateBrowserClient();
        var isDevelopment = environment == "Development";

        var checkedCount = 0;
        foreach (var (key, method, path) in GetEndpoints(factory))
        {
            if (AnonymousEverywhere.Contains(key) || (isDevelopment && IsDevelopmentOnly(key)))
            {
                continue;
            }

            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using var response = await client.SendAsync(request, Ct);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"{key} answered without a session");
            checkedCount++;
        }

        // Guards against the enumeration silently finding nothing
        Assert.IsGreaterThan(15, checkedCount);
    }

    [TestMethod]
    public async Task TheApiDocsAreOnlyMappedInDevelopment()
    {
        await using var factory = new LookoutFactory("Production");

        var keys = GetEndpoints(factory).Select(e => e.Key).ToList();

        Assert.IsFalse(keys.Exists(IsDevelopmentOnly), string.Join(", ", keys));
    }

    [TestMethod]
    public async Task AnUnknownApiRouteIsNotFound()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);

        using var response = await client.GetAsync("/api/nothing-here", Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Every routed endpoint as "METHOD pattern", with each method it accepts and a path that reaches it.
    /// </summary>
    private static IEnumerable<(string Key, string Method, string Path)> GetEndpoints(LookoutFactory factory)
    {
        // Creating the server builds the app, which maps the endpoints
        _ = factory.Server;
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var pattern = endpoint.RoutePattern.RawText ?? "";
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods is null or { Count: 0 })
            {
                yield return ($"* {pattern}", "GET", ToPath(pattern));
                continue;
            }

            foreach (var method in methods)
            {
                yield return ($"{method} {pattern}", method, ToPath(pattern));
            }
        }
    }

    /// <summary>
    /// Fills each route parameter with a value that satisfies its constraints, like <c>{id:int}</c>.
    /// </summary>
    private static string ToPath(string pattern) =>
        "/" + RouteParameter().Replace(pattern.TrimStart('/'), "1");

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();
}
