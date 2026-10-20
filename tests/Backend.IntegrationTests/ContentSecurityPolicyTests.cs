using Microsoft.AspNetCore.Hosting;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// A second line of defence against cross-site scripting, as Angular's security guide recommends: only Lookout's own
/// scripts run, Angular's Trusted Types policies guard the DOM, and no other site can frame the app.
/// </summary>
[TestClass]
public sealed class ContentSecurityPolicyTests(TestContext testContext)
{
    private static readonly string[] RequiredDirectives =
    [
        "default-src 'self'",
        "script-src 'self'",
        // Angular adds component styles as <style> elements, but nothing needs inline style="…" attributes.
        "style-src-elem 'self' 'unsafe-inline'",
        "style-src-attr 'none'",
        "object-src 'none'",
        "base-uri 'self'",
        "frame-ancestors 'none'",
        "require-trusted-types-for 'script'",
        "trusted-types angular angular#bundler"
    ];

    [TestMethod]
    [DataRow("/")]
    [DataRow("/some-page")]
    [DataRow("/api/session")]
    public async Task RestrictsWhatTheAppCanLoadAndRun(string path)
    {
        var webRoot = Directory.CreateTempSubdirectory("lookout-csp-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<h1>Lookout app</h1>", testContext.CancellationToken);
            await using var factory = new LookoutWebApplicationFactory();
            await using var host = factory.WithWebHostBuilder(builder => builder.UseWebRoot(webRoot));
            using var client = host.CreateClient();

            var response = await client.GetAsync(new Uri(path, UriKind.Relative), testContext.CancellationToken);

            var policy = response.Headers.GetValues("Content-Security-Policy").Single()
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            CollectionAssert.IsSubsetOf(RequiredDirectives, policy);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }
}
