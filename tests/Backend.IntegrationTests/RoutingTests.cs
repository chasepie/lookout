using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class RoutingTests(TestContext testContext)
{
    [TestMethod]
    [DataRow(true, HttpStatusCode.TemporaryRedirect)]
    [DataRow(false, HttpStatusCode.OK)]
    public async Task RedirectsHttpOnlyWhenHttpsIsConfigured(bool httpsConfigured, HttpStatusCode expected)
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            if (httpsConfigured) services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 7443);
        }));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(new Uri("/api/watches", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(expected, response.StatusCode);
        Assert.AreEqual(httpsConfigured ? new Uri("https://localhost:7443/api/watches") : null, response.Headers.Location);
    }

    [TestMethod]
    [DataRow("GET", "/api", HttpStatusCode.NotFound)]
    [DataRow("GET", "/api/watches/not-a-guid/runs", HttpStatusCode.NotFound)]
    [DataRow("GET", "/API/unknown", HttpStatusCode.NotFound)]
    [DataRow("DELETE", "/api/watches", HttpStatusCode.MethodNotAllowed)]
    [DataRow("GET", "/some-page", HttpStatusCode.OK)]
    [DataRow("POST", "/some-page", HttpStatusCode.MethodNotAllowed)]
    public async Task OnlyServesTheAppForPageRequests(string method, string path, HttpStatusCode expected)
    {
        var webRoot = Directory.CreateTempSubdirectory("lookout-routing-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<h1>Lookout app</h1>", testContext.CancellationToken);
            await using var factory = new LookoutWebApplicationFactory();
            await using var host = factory.WithWebHostBuilder(builder => builder.UseWebRoot(webRoot));
            using var client = host.CreateClient();
            using var request = new HttpRequestMessage(new HttpMethod(method), path);

            var response = await client.SendAsync(request, testContext.CancellationToken);

            Assert.AreEqual(expected, response.StatusCode);
            if (expected == HttpStatusCode.OK)
            {
                Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
                Assert.AreEqual("<h1>Lookout app</h1>", await response.Content.ReadAsStringAsync(testContext.CancellationToken));
            }
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }
}
