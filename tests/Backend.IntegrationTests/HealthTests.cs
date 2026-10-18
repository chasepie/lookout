using System.Net;
using ChasePie.Lookout.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

public sealed class HealthTests
{
    [Fact]
    public async Task ReportsHealthyWhenTheDatabaseIsReachable()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, $"Expected success but got {(int)response.StatusCode}");
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportsUnhealthyWhenTheDatabaseIsCorrupt()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();
        await File.WriteAllTextAsync(DatabasePath(factory), new string('x', 4096), TestContext.Current.CancellationToken);

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static string DatabasePath(LookoutWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Database.GetDbConnection().DataSource;
    }
}
