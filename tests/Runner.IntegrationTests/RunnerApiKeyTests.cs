using System.Net;
using System.Text.RegularExpressions;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Runner.IntegrationTests;

/// <summary>
/// That the Runner only answers callers with the key, except on the few endpoints meant to be open, so a new endpoint
/// can't be left open by mistake. Adding an anonymous endpoint means adding it here on purpose.
/// </summary>
[TestClass]
public sealed partial class RunnerApiKeyTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    // A name the Runner refuses before running anything, so a request that gets past the key answers 400 without Node
    private const string RunRequest = "/WatchRun?scriptName=..%2Fscript.ts";

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
    public async Task ARunWithTheKeyReachesTheRunner()
    {
        await using var factory = new RunnerFactory();
        using var client = factory.CreateClient(RunnerFactory.ApiKey);

        using var response = await client.GetAsync(RunRequest, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Keys that differ from RunnerFactory.ApiKey only slightly, or not at all in length
    public static IEnumerable<object?[]> WrongKeys =>
    [
        [null],
        [""],
        ["wrong"],
        [RunnerFactory.ApiKey[..^1] + "K"],
        [RunnerFactory.ApiKey + "k"],
        [RunnerFactory.ApiKey[..^1]],
    ];

    [TestMethod]
    [DynamicData(nameof(WrongKeys))]
    public async Task ARunWithoutTheKeyIsRefused(string? apiKey)
    {
        await using var factory = new RunnerFactory();
        using var client = factory.CreateClient(apiKey);

        using var response = await client.GetAsync(RunRequest, Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ARepeatedKeyHeaderIsRefused()
    {
        await using var factory = new RunnerFactory();
        using var client = factory.CreateClient(apiKey: null);
        using var request = new HttpRequestMessage(HttpMethod.Get, RunRequest);
        request.Headers.Add(RunnerApiKey.HeaderName, [RunnerFactory.ApiKey, "wrong"]);

        using var response = await client.SendAsync(request, Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [DataRow("Production")]
    [DataRow("Development")]
    public async Task EveryEndpointNeedsTheKeyUnlessItIsMeantToBeOpen(string environment)
    {
        await using var factory = new RunnerFactory(environment);
        using var client = factory.CreateClient(apiKey: null);
        var isDevelopment = environment == "Development";

        var checkedCount = 0;
        foreach (var (key, pattern, method, path) in GetEndpoints(factory))
        {
            if (isDevelopment && IsDevelopmentOnly(key))
            {
                continue;
            }

            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using var response = await client.SendAsync(request, Ct);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"{key} answered without the key");

            // The fallback policy also answers 401 for a path that matches no endpoint, so a path that misses the
            // endpoint would prove nothing about it
            AssertReached(response, pattern, path, key);
            checkedCount++;
        }

        // Guards against the enumeration silently finding nothing
        Assert.IsGreaterThan(0, checkedCount);
    }

    [TestMethod]
    public async Task TheHealthChecksAndApiDocsAreOpenInDevelopment()
    {
        await using var factory = new RunnerFactory("Development");
        using var client = factory.CreateClient(apiKey: null);

        foreach (var path in new[] { "/health", "/alive", "/openapi/v1.json", "/api-docs/" })
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode, path);
        }
    }

    [TestMethod]
    public async Task TheApiDocsAndHealthChecksAreOnlyMappedInDevelopment()
    {
        await using var factory = new RunnerFactory("Production");

        var keys = GetEndpoints(factory).Select(e => e.Key).ToList();

        Assert.IsFalse(keys.Exists(IsDevelopmentOnly), string.Join(", ", keys));
    }

    /// <summary>
    /// Every routed endpoint as "METHOD pattern", with its pattern, each method it accepts and a path meant to reach it.
    /// </summary>
    private static void AssertReached(HttpResponseMessage response, string pattern, string path, string key) =>
        Assert.AreEqual(
            pattern,
            response.Headers.TryGetValues(RunnerFactory.MatchedRouteHeader, out var matched) ? matched.Single() : null,
            $"{path} doesn't reach {key}; fix ToPath");

    private static IEnumerable<(string Key, string Pattern, string Method, string Path)> GetEndpoints(RunnerFactory factory)
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
                yield return ($"* {pattern}", pattern, "GET", ToPath(pattern));
                continue;
            }

            foreach (var method in methods)
            {
                yield return ($"{method} {pattern}", pattern, method, ToPath(pattern));
            }
        }
    }

    /// <summary>
    /// Fills each route parameter with "1", which satisfies constraints like <c>{id:int}</c> but not all of them; the
    /// test checks that each path reaches its endpoint.
    /// </summary>
    private static string ToPath(string pattern) =>
        "/" + RouteParameter().Replace(pattern.TrimStart('/'), "1");

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();
}
