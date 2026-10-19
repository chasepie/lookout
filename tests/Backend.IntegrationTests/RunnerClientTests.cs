using System.Diagnostics;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class RunnerClientTests(TestContext testContext)
{
    [TestMethod]
    public async Task ExecutesTypeScriptThroughTheRealRunnerAndPassesTheNotificationUrl()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Lookout.slnx")))
        {
            repository = repository.Parent;
        }
        Assert.IsNotNull(repository);
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(repository.FullName, "src/runner/index.ts"));
        start.Environment["PORT"] = "0";
        start.Environment["HEADLESS"] = "true";
        using var process = Process.Start(start)!;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.IsNotNull(line);
            var address = new Uri(line["Runner listening on ".Length..]);
            using var http = new HttpClient { BaseAddress = address };
            var runner = new HttpRunnerClient(http, TimeProvider.System);

            var result = await runner.ExecuteAsync(new RunnerRequest("""
                import type { Page } from 'playwright';
                export default {
                  async run(page: Page, context: { notificationUrl: string }) {
                    await page.setContent('<p>£19.99</p>');
                    return { success: true, content: await page.locator('p').innerText(), details: context.notificationUrl };
                  }
                };
                """, "ts", new ScriptContext("https://example.com/widget")), timeout.Token);

            Assert.AreEqual(RunOutcome.Succeeded, result.Outcome, result.ErrorMessage);
            Assert.AreEqual("£19.99", result.Content);
            Assert.AreEqual("https://example.com/widget", result.Details);
            Assert.IsGreaterThan(0, result.DurationMs);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
