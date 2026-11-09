using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Runner.IntegrationTests;

/// <summary>
/// What the Runner makes of what the watch CLI writes and how it exits, with <see cref="RunnerFactory.StandInCli"/> in
/// its place.
/// </summary>
[TestClass]
public sealed class RunResultTests
{
    private static readonly string[] SuccessAndCrashFlags = ["--screenshot-on-success", "--screenshot-on-crash"];

    // Only the CLI's output is under test, so a PNG's first bytes do for a screenshot
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task TheWatchsScreenshotSettingsArePassedToTheCli()
    {
        var result = await RunAsync(
            new { echoFlags = true },
            "screenshotOnSuccess=true&screenshotOnKnownFailure=false&screenshotOnCrash=true");

        var flags = JsonSerializer.Deserialize<string[]>(result.Content!);
        Assert.AreSequenceEqual(SuccessAndCrashFlags, flags);
    }

    [TestMethod]
    public async Task AResultsScreenshotIsReturnedWithIt()
    {
        var result = await RunAsync(new
        {
            write = new { success = true, content = "Hello", screenshot = Convert.ToBase64String(PngSignature) },
        });

        Assert.AreEqual("Hello", result.Content);
        Assert.AreSequenceEqual(PngSignature, result.Screenshot!);
    }

    [TestMethod]
    public async Task ACrashIsReturnedWithItsErrorAndScreenshot()
    {
        var result = await RunAsync(new
        {
            write = new
            {
                crash = new { error = "Error: No price on the page\n    at run (watch.ts:4:13)" },
                screenshot = Convert.ToBase64String(PngSignature),
            },
            exitCode = 1,
        });

        Assert.AreEqual("Error: No price on the page\n    at run (watch.ts:4:13)", result.Crash?.Error);
        Assert.AreSequenceEqual(PngSignature, result.Screenshot!);
    }

    [TestMethod]
    public async Task SecretsAreRedactedFromACrashsError()
    {
        // Playwright's errors can hold the browserless URL, token included
        var result = await RunAsync(new
        {
            write = new { crash = new { error = $"Error: Couldn't reach {RunnerFactory.BrowserlessUrl}?token={RunnerFactory.BrowserlessToken}" } },
            exitCode = 1,
        });

        Assert.AreEqual($"Error: Couldn't reach {RunnerFactory.BrowserlessUrl}?token=***", result.Crash?.Error);
    }

    /// <summary>
    /// Has the stand-in CLI follow <paramref name="instructions"/>, and returns the run's result.
    /// </summary>
    private async Task<WatchRunResult> RunAsync(object instructions, string query = "")
    {
        await using var factory = new RunnerFactory(standInCli: true);
        Directory.CreateDirectory(factory.ScriptsDirectory);
        await File.WriteAllTextAsync(
            Path.Join(factory.ScriptsDirectory, "watch.ts"), JsonSerializer.Serialize(instructions), Ct);
        using var client = factory.CreateClient(RunnerFactory.ApiKey);

        using var response = await client.GetAsync($"/WatchRun?scriptName=watch.ts&{query}", Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<WatchRunResult>(JsonSerializerOptions.Web, Ct))!;
    }
}
