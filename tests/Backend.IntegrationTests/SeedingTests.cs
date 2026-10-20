using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class SeedingTests(TestContext testContext)
{
    [TestMethod]
    public async Task SeedsTheBotDetectionExampleIntoANewDevelopmentDatabase()
    {
        await using var factory = new LookoutWebApplicationFactory();
        RunnerRequest? executed = null;
        factory.Runner.OnExecute = (request, _) =>
        {
            executed = request;
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "Normal", DurationMs = 1 });
        };
        await using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);

        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);

        Assert.HasCount(1, watches!);
        var watch = watches![0]!;
        Assert.AreEqual("Bot detection example", watch["name"]!.GetValue<string>());
        Assert.AreEqual("https://www.browserscan.net/bot-detection", watch["notificationUrl"]!.GetValue<string>());
        Assert.AreEqual("interval", watch["schedule"]!["kind"]!.GetValue<string>());
        Assert.AreEqual(1, watch["schedule"]!["every"]!.GetValue<int>());
        Assert.AreEqual("hours", watch["schedule"]!["unit"]!.GetValue<string>());
        Assert.AreEqual(factory.Time.GetUtcNow().AddHours(1), watch["nextRunAt"]!.GetValue<DateTimeOffset>());

        var run = await client.PostAsync(new Uri($"/api/watches/{watch["id"]}/runs", UriKind.Relative), null, testContext.CancellationToken);

        run.EnsureSuccessStatusCode();
        var example = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot.Find(), "examples/bot-detection.ts"), testContext.CancellationToken);
        Assert.AreEqual(example, executed!.Source);
        Assert.AreEqual("ts", executed.FileKind);
    }

    [TestMethod]
    [DataRow("Testing")]
    [DataRow("Production")]
    public async Task StartsOtherEnvironmentsWithoutWatches(string environment)
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);

        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);

        Assert.IsEmpty(watches!);
    }

    [TestMethod]
    public async Task SeedsOnlyWhenTheDatabaseIsCreated()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var databasePath = Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");
        try
        {
            await using (var first = Development(factory, databasePath))
            {
                using var client = await first.CreateSignedInClientAsync(testContext.CancellationToken);
                Assert.HasCount(1, (await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken))!);
                // The user removes the example; there is no delete endpoint yet.
                await using var scope = first.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Watches
                    .ExecuteDeleteAsync(testContext.CancellationToken);
            }

            await using var restarted = Development(factory, databasePath);
            using var again = await restarted.CreateLoggedInClientAsync(testContext.CancellationToken);

            Assert.IsEmpty((await again.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken))!);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Development(
        LookoutWebApplicationFactory factory, string databasePath) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Lookout", $"Data Source={databasePath};Pooling=False");
        });
}
