using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class RunnerClientTests(TestContext testContext)
{
    [TestMethod]
    [DataRow("http://proxy/runner/")]
    [DataRow("http://proxy/runner")]
    public async Task PreservesTheConfiguredRunnerBasePath(string baseUrl)
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Runner:BaseUrl", baseUrl);
            builder.ConfigureTestServices(services => services.AddHttpClient<IRunnerClient, HttpRunnerClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new RunnerPathHandler()));
        });
        var runner = host.Services.GetRequiredService<IRunnerClient>();

        var result = await runner.ExecuteAsync(new RunnerRequest("export default {}", "ts", new ScriptContext("https://example.com")), testContext.CancellationToken);

        Assert.AreEqual(RunOutcome.Succeeded, result.Outcome);
    }

    private sealed class RunnerPathHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(new Uri("http://proxy/runner/execute"), request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"outcome\":\"succeeded\",\"content\":\"£20\",\"durationMs\":1}")
            });
        }
    }

    [TestMethod]
    public async Task ExecutesTypeScriptThroughTheRealRunnerAndPassesTheNotificationUrl()
    {
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(RepositoryRoot.Find(), "src/runner/index.ts"));
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
