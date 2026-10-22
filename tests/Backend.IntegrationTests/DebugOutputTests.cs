using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class DebugOutputTests(TestContext testContext)
{
    // A 1×1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [TestMethod]
    [DataRow(RunOutcome.KnownFailure)]
    [DataRow(RunOutcome.Errored)]
    public async Task StoresAndServesTheConsoleOutputAndScreenshotOfAKnownFailureOrErroredRun(RunOutcome outcome)
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult
        {
            Outcome = outcome, ErrorMessage = "No price", DurationMs = 1,
            ConsoleOutput = "Loading page\nNo price found\n", Screenshot = Convert.ToBase64String(Png)
        });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watchId = await CreateWatch(client);

        var runId = await RunNow(client, watchId);
        var debugOutput = await client.GetFromJsonAsync<JsonObject>(
            $"/api/watches/{watchId}/runs/{runId}/debug-output", testContext.CancellationToken);
        using var screenshot = await client.GetAsync(
            new Uri($"/api/watches/{watchId}/runs/{runId}/screenshot", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual("Loading page\nNo price found\n", debugOutput!["consoleOutput"]!.GetValue<string>());
        Assert.IsTrue(debugOutput["hasScreenshot"]!.GetValue<bool>());
        Assert.AreEqual(HttpStatusCode.OK, screenshot.StatusCode);
        Assert.AreEqual("image/png", screenshot.Content.Headers.ContentType?.MediaType);
        // A Run's screenshot never changes, so the browser needn't download it again.
        Assert.IsTrue(screenshot.Headers.CacheControl?.Private);
        Assert.Contains("immutable", screenshot.Headers.CacheControl!.Extensions.Select(extension => extension.Name));
        Assert.AreSequenceEqual(Png, await screenshot.Content.ReadAsByteArrayAsync(testContext.CancellationToken));
    }

    [TestMethod]
    public async Task StoresTheDebugOutputTheRunnerSendsBack()
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services
            .AddHttpClient<IRunnerClient, HttpRunnerClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new ErroredRunnerHandler(Convert.ToBase64String(Png)))));
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        var watchId = await CreateWatch(client);

        var runId = await RunNow(client, watchId);
        var debugOutput = await client.GetFromJsonAsync<JsonObject>(
            $"/api/watches/{watchId}/runs/{runId}/debug-output", testContext.CancellationToken);
        var screenshot = await client.GetByteArrayAsync(
            new Uri($"/api/watches/{watchId}/runs/{runId}/screenshot", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual("Looking for the price\n", debugOutput!["consoleOutput"]!.GetValue<string>());
        Assert.AreSequenceEqual(Png, screenshot);
    }

    [TestMethod]
    public async Task KeepsNoDebugOutputForASucceededRun()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult
        {
            Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1, ConsoleOutput = "Found £20\n"
        });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watchId = await CreateWatch(client);

        var runId = await RunNow(client, watchId);

        Assert.AreEqual(HttpStatusCode.NotFound, await DebugOutputStatus(client, watchId, runId));
    }

    [TestMethod]
    public async Task KeepsDebugOutputForOnlyTheMostRecentTwentyKnownFailureOrErroredRunsOfEachWatch()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = new RunnerResult
        {
            Outcome = RunOutcome.Errored, ErrorMessage = "No price", DurationMs = 1, ConsoleOutput = "No price found\n"
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watchId = await CreateWatch(client);
        var otherWatchId = await CreateWatch(client);
        var otherRunId = await RunNow(client, otherWatchId);
        var runIds = new List<long>();
        for (var run = 0; run < 21; run++)
        {
            if (run == 10)
            {
                // A Succeeded Run in between neither keeps debug output nor counts towards the twenty.
                factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
                await RunNow(client, watchId);
            }
            runIds.Add(await RunNow(client, watchId));
        }

        Assert.AreEqual(HttpStatusCode.NotFound, await DebugOutputStatus(client, watchId, runIds[0]));
        foreach (var runId in runIds.Skip(1))
        {
            Assert.AreEqual(HttpStatusCode.OK, await DebugOutputStatus(client, watchId, runId));
        }
        Assert.AreEqual(HttpStatusCode.OK, await DebugOutputStatus(client, otherWatchId, otherRunId));
        var history = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{watchId}/runs", testContext.CancellationToken);
        Assert.HasCount(22, history!);
    }

    [TestMethod]
    public async Task ServesARunsDebugOutputOnlyUnderItsOwnWatch()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = new RunnerResult
        {
            Outcome = RunOutcome.Errored, ErrorMessage = "No price", DurationMs = 1, ConsoleOutput = "No price found\n",
            Screenshot = Convert.ToBase64String(Png)
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watchId = await CreateWatch(client);
        var otherWatchId = await CreateWatch(client);
        var runId = await RunNow(client, watchId);

        using var screenshot = await client.GetAsync(
            new Uri($"/api/watches/{otherWatchId}/runs/{runId}/screenshot", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, await DebugOutputStatus(client, otherWatchId, runId));
        Assert.AreEqual(HttpStatusCode.NotFound, screenshot.StatusCode);
    }

    private async Task<HttpStatusCode> DebugOutputStatus(HttpClient client, string watchId, long runId)
    {
        using var response = await client.GetAsync(
            new Uri($"/api/watches/{watchId}/runs/{runId}/debug-output", UriKind.Relative), testContext.CancellationToken);
        return response.StatusCode;
    }

    private async Task<string> CreateWatch(HttpClient client)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Widget price"), "name" },
            { new StringContent("https://example.com/widget"), "notificationUrl" },
            { new StringContent("export default { async run() { return { success: true, content: '£20' }; } };"), "script", "price.ts" },
            { new StringContent("interval"), "scheduleKind" },
            { new StringContent("1"), "every" },
            { new StringContent("hours"), "unit" }
        };
        var response = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        var watch = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        return watch!["id"]!.GetValue<string>();
    }

    private async Task<long> RunNow(HttpClient client, string watchId)
    {
        var response = await client.PostAsync(new Uri($"/api/watches/{watchId}/runs", UriKind.Relative), null, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        var run = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        return run!["id"]!.GetValue<long>();
    }

    /// <summary>Answers every execution as the runner does for a Script that threw.</summary>
    private sealed class ErroredRunnerHandler(string screenshot) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    outcome = "errored", errorMessage = "No price", durationMs = 12,
                    consoleOutput = "Looking for the price\n", screenshot
                })
            });
        }
    }
}
