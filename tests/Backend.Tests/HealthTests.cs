namespace ChasePie.Lookout.Backend.Tests;

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
}
