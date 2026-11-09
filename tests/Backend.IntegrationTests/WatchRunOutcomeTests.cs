using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChasePie.Lookout.Backend.Controllers;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// What a run keeps of how its script ended, and of its screenshot, with the Runner faked at its HTTP boundary.
/// </summary>
[TestClass]
public sealed class WatchRunOutcomeTests
{
    // A PNG's first bytes, which is all the Backend looks at
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task TheWatchsScreenshotSettingsAreSavedAndSentToTheRunner()
    {
        var runner = new FakeRunner(new { success = true, content = "Hello" });
        await using var factory = CreateFactory(runner);
        using var client = await factory.CreateSignedInClientAsync(Ct);

        var watchId = await CreateWatchAsync(client, screenshotOnSuccess: true, screenshotOnCrash: true);
        await RunAsync(client, watchId);

        var watch = await client.GetFromJsonAsync<WatchSummary>($"/api/Watches/{watchId}", Ct);
        Assert.IsTrue(watch!.ScreenshotOnSuccess);
        Assert.IsFalse(watch.ScreenshotOnKnownFailure);
        Assert.IsTrue(watch.ScreenshotOnCrash);
        var query = runner.Requests.Single().Query;
        Assert.Contains("screenshotOnSuccess=true", query);
        Assert.Contains("screenshotOnKnownFailure=false", query);
        Assert.Contains("screenshotOnCrash=true", query);
    }

    [TestMethod]
    [DataRow(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, "image/png")]
    [DataRow(new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10 }, "image/jpeg")]
    public async Task ASuccessKeepsItsScreenshotAndItsType(byte[] screenshot, string mediaType)
    {
        var runner = new FakeRunner(new { success = true, content = "Hello", screenshot });
        await using var factory = CreateFactory(runner);
        using var client = await factory.CreateSignedInClientAsync(Ct);

        var run = await RunAsync(client, await CreateWatchAsync(client));

        Assert.AreEqual("Hello", run.Content);
        Assert.IsNull(run.Error);
        Assert.AreSequenceEqual(screenshot, run.Screenshot!);
        Assert.AreEqual(mediaType, run.ScreenshotMediaType);
    }

    [TestMethod]
    public async Task AKnownFailureIsKeptAsItsReason()
    {
        var runner = new FakeRunner(new { success = false, failureReason = "Blocked in this region" });
        await using var factory = CreateFactory(runner);
        using var client = await factory.CreateSignedInClientAsync(Ct);

        var watchId = await CreateWatchAsync(client);
        var run = await RunAsync(client, watchId);

        Assert.AreEqual("Blocked in this region", run.FailureReason);
        Assert.IsNull(run.Error);
        var summary = (await client.GetFromJsonAsync<List<WatchRunSummary>>($"/api/Watches/{watchId}/runs", Ct))!.Single();
        Assert.AreEqual("Blocked in this region", summary.FailureReason);
        Assert.IsNull(summary.Error);
    }

    [TestMethod]
    public async Task ACrashIsKeptAsItsErrorWithItsScreenshot()
    {
        var runner = new FakeRunner(new { crash = new { error = "Error: No price on the page" }, screenshot = Png });
        await using var factory = CreateFactory(runner);
        using var client = await factory.CreateSignedInClientAsync(Ct);

        var run = await RunAsync(client, await CreateWatchAsync(client));

        Assert.AreEqual("Error: No price on the page", run.Error);
        Assert.IsNull(run.FailureReason);
        Assert.IsNull(run.Content);
        Assert.AreSequenceEqual(Png, run.Screenshot!);
    }

    [TestMethod]
    public async Task AScreenshotThatIsntAPngOrJpegMakesTheRunACrash()
    {
        var runner = new FakeRunner(new { success = true, content = "Hello", screenshot = "GIF89a"u8.ToArray() });
        await using var factory = CreateFactory(runner);
        using var client = await factory.CreateSignedInClientAsync(Ct);

        var run = await RunAsync(client, await CreateWatchAsync(client));

        Assert.AreEqual("The script's screenshot isn't a PNG or JPEG image.", run.Error);
        Assert.IsNull(run.Content);
        Assert.IsNull(run.Screenshot);
        Assert.IsNull(run.ScreenshotMediaType);
    }

    [TestMethod]
    public async Task AScreenshotOver8MiBMakesTheRunACrash()
    {
        var screenshot = new byte[(8 * 1024 * 1024) + 1];
        Png.CopyTo(screenshot, 0);
        var runner = new FakeRunner(new { success = false, failureReason = "Blocked", screenshot });
        await using var factory = CreateFactory(runner);
        using var client = await factory.CreateSignedInClientAsync(Ct);

        var run = await RunAsync(client, await CreateWatchAsync(client));

        Assert.AreEqual("The script's screenshot is 8.0 MiB, over the 8 MiB a run can keep.", run.Error);
        Assert.IsNull(run.FailureReason);
        Assert.IsNull(run.Screenshot);
    }

    private static LookoutFactory CreateFactory(FakeRunner runner) => new(configureServices: services =>
        services.AddHttpClient<RunnerHttpClient>().ConfigurePrimaryHttpMessageHandler(() => runner));

    private async Task<int> CreateWatchAsync(
        HttpClient client,
        bool screenshotOnSuccess = false,
        bool screenshotOnKnownFailure = false,
        bool screenshotOnCrash = false)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Watch"), "Name" },
            { new StringContent(screenshotOnSuccess ? "true" : "false"), "ScreenshotOnSuccess" },
            { new StringContent(screenshotOnKnownFailure ? "true" : "false"), "ScreenshotOnKnownFailure" },
            { new StringContent(screenshotOnCrash ? "true" : "false"), "ScreenshotOnCrash" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("export default {};")), "script", "watch.ts" },
        };
        using var response = await client.PostAsync("/api/Watches", form, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var watch = await response.Content.ReadFromJsonAsync<Watch>(Ct);
        return watch!.Id;
    }

    /// <summary>
    /// Creates a run of the watch and waits for the worker to finish it.
    /// </summary>
    private async Task<WatchRun> RunAsync(HttpClient client, int watchId)
    {
        using var response = await client.PostAsync($"/api/Watches/{watchId}/runs", null, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var runId = (await response.Content.ReadFromJsonAsync<WatchRun>(Ct))!.Id;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var run = await client.GetFromJsonAsync<WatchRun>($"/api/Watches/{watchId}/runs/{runId}", timeout.Token);
            if (run!.Status == WatchRunStatus.Done)
            {
                return run;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }

    /// <summary>
    /// Answers every run with <paramref name="response"/> as the Runner's JSON, and keeps the requests.
    /// </summary>
    private sealed class FakeRunner(object response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(response, JsonSerializerOptions.Web), Encoding.UTF8, "application/json"),
            });
        }
    }
}
