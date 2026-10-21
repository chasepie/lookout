using System.Net;
using ChasePie.Lookout.Database;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class HealthTests(TestContext testContext)
{
    [TestMethod]
    public async Task ReportsHealthyWhenTheDatabaseIsReachable()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), testContext.CancellationToken);

        Assert.IsTrue(response.IsSuccessStatusCode, $"Expected success but got {(int)response.StatusCode}");
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync(testContext.CancellationToken));
    }

    [TestMethod]
    public async Task ReportsUnhealthyWhenTheDatabaseIsCorrupt()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();
        await File.WriteAllTextAsync(DatabasePath(factory), new string('x', 4096), testContext.CancellationToken);

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual("Unhealthy", await response.Content.ReadAsStringAsync(testContext.CancellationToken));
    }

    [TestMethod]
    public async Task ReportsUnhealthyWhenTheDatabaseIsMissing()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();
        File.Delete(DatabasePath(factory));

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual("Unhealthy", await response.Content.ReadAsStringAsync(testContext.CancellationToken));
    }

    [TestMethod]
    public async Task AnswersOverHttpEvenWhenHttpsIsConfigured()
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 7443)));
        // A health probe, such as a container's, may not follow redirects, and may count one as healthy.
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync(testContext.CancellationToken));
    }

    private static string DatabasePath(LookoutWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Database.GetDbConnection().DataSource;
    }
}
