using System.Net;
using ChasePie.Lookout.Database;
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

    private static string DatabasePath(LookoutWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Database.GetDbConnection().DataSource;
    }
}
