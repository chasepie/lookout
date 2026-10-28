using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
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
            builder.UseSetting("Runner:Token", "runner-secret");
            builder.ConfigureTestServices(services => services.AddHttpClient<IRunnerClient, HttpRunnerClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new RunnerPathHandler()));
        });
        var runner = host.Services.GetRequiredService<IRunnerClient>();

        var result = await runner.ExecuteAsync(new RunnerRequest("export default {}", "ts", new ScriptContext("https://example.com")), testContext.CancellationToken);

        Assert.AreEqual(RunOutcome.Succeeded, result.Outcome);
    }

    [TestMethod]
    public async Task SendsTheConfiguredRunnerTokenWithEveryRequest()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var handler = new RunnerPathHandler();
        await using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Runner:BaseUrl", "http://proxy/runner/");
            builder.UseSetting("Runner:Token", "runner-secret");
            builder.ConfigureTestServices(services => services.AddHttpClient<IRunnerClient, HttpRunnerClient>()
                .ConfigurePrimaryHttpMessageHandler(() => handler));
        });
        var runner = host.Services.GetRequiredService<IRunnerClient>();

        await runner.ExecuteAsync(new RunnerRequest("export default {}", "ts", new ScriptContext("https://example.com")), testContext.CancellationToken);

        Assert.AreEqual("Bearer runner-secret", handler.Authorization);
    }

    [TestMethod]
    public async Task RefusesToStartWithARunnerTokenThatCouldChangeTheHeader()
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Runner:Token", "secret\r\nX-Other: 1"));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains("Runner:Token", error.Message);
    }

    [TestMethod]
    [DataRow(null, DisplayName = "No token")]
    [DataRow("", DisplayName = "An empty token, as compose passes an unset variable")]
    public async Task RefusesToStartWithoutARunnerTokenForARunnerOnAnotherMachine(string? token)
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Runner:BaseUrl", "http://runner:3000");
            builder.UseSetting("Runner:Token", token);
        });

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains("Runner:Token", error.Message);
    }

    [TestMethod]
    [DataRow("localhost:3000")]
    [DataRow("not a URL")]
    [DataRow("ftp://runner")]
    public async Task RefusesToStartWithAnInvalidRunnerBaseUrl(string baseUrl)
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Runner:BaseUrl", baseUrl));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains("Runner:BaseUrl", error.Message);
    }

    [TestMethod]
    public async Task WaitsForTheScriptTimeoutPlusAGraceBeforeGivingUpOnTheRunner()
    {
        var timeProvider = new ShortTimerProvider();
        using var http = new HttpClient(new UnresponsiveHandler()) { BaseAddress = new Uri("http://runner/"), Timeout = Timeout.InfiniteTimeSpan };
        var runner = new HttpRunnerClient(http, timeProvider);

        var result = await runner.ExecuteAsync(new RunnerRequest("export default {}", "ts", new ScriptContext("https://example.com"), TimeoutMs: 180_000),
            testContext.CancellationToken);

        Assert.AreEqual(TimeSpan.FromSeconds(195), timeProvider.RequestedDueTime);
        Assert.AreEqual(RunOutcome.Errored, result.Outcome);
        Assert.Contains("did not respond within 195 seconds", result.ErrorMessage!);
    }

    /// <summary>Records the timer it is asked for, then fires it almost at once.</summary>
    private sealed class ShortTimerProvider : TimeProvider
    {
        public TimeSpan? RequestedDueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            RequestedDueTime = dueTime;
            return base.CreateTimer(callback, state, TimeSpan.FromMilliseconds(10), period);
        }
    }

    private sealed class UnresponsiveHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class RunnerPathHandler : HttpMessageHandler
    {
        /// <summary>The Authorization header of the last request, if it had one.</summary>
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(new Uri("http://proxy/runner/execute"), request.RequestUri);
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"outcome\":\"succeeded\",\"content\":\"£20\",\"durationMs\":1}")
            });
        }
    }

    [TestMethod]
    public async Task ExecutesTypeScriptThroughTheRealRunnerAndReturnsItsScreenshotAndConsoleOutput()
    {
        await WithRealRunner(async (runner, cancellationToken) =>
        {
            var result = await runner.ExecuteAsync(new RunnerRequest("""
                import type { Page } from 'playwright';
                export default {
                  async run(page: Page, context: { notificationUrl: string }) {
                    await page.setContent('<p>£19.99</p>');
                    console.log('Reading the price');
                    return { success: true, content: await page.locator('p').innerText(), details: context.notificationUrl };
                  }
                };
                """, "ts", new ScriptContext("https://example.com/widget"), AlwaysScreenshot: true), cancellationToken);

            Assert.AreEqual(RunOutcome.Succeeded, result.Outcome, result.ErrorMessage);
            Assert.AreEqual("£19.99", result.Content);
            Assert.AreEqual("https://example.com/widget", result.Details);
            Assert.IsGreaterThan(0, result.DurationMs);
            Assert.AreEqual("Reading the price\n", result.ConsoleOutput);
            Assert.IsTrue(Convert.FromBase64String(result.Screenshot!).AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47]));
        });
    }

    [TestMethod]
    [DataRow("lookout", "secret", DisplayName = "With credentials")]
    [DataRow(null, null, DisplayName = "Without credentials")]
    public async Task ARunWithProxySettingsGoesThroughTheProxyWithTheRealRunner(string? username, string? password)
    {
        await using var proxy = await StartProxyAsync(username, password, testContext.CancellationToken);
        await WithRealRunner(async (runner, cancellationToken) =>
        {
            var result = await runner.ExecuteAsync(new RunnerRequest("""
                import type { Page } from 'playwright';
                export default {
                  async run(page: Page, context: { notificationUrl: string }) {
                    await page.goto(context.notificationUrl);
                    return { success: true, content: await page.locator('p').innerText() };
                  }
                };
                """, "ts", new ScriptContext("http://shop.lookout.test/widget"),
                Proxy: new ProxySettings(proxy.BaseAddress.GetLeftPart(UriPartial.Authority), username, password)), cancellationToken);

            Assert.AreEqual(RunOutcome.Succeeded, result.Outcome, result.ErrorMessage);
            Assert.AreEqual("Proxied http://shop.lookout.test/widget", result.Content);
        });
    }

    /// <summary>
    /// An HTTP proxy that answers every request itself, naming the URL it was asked for. Given credentials, it
    /// answers only requests that carry them.
    /// </summary>
    private static async Task<FakeHttpServer> StartProxyAsync(string? username, string? password, CancellationToken cancellationToken)
    {
        var proxy = await FakeHttpServer.StartAsync(cancellationToken);
        var expected = username is null ? null : $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))}";
        proxy.Respond = async context =>
        {
            if (expected is not null && context.Request.Headers.ProxyAuthorization != expected)
            {
                context.Response.StatusCode = StatusCodes.Status407ProxyAuthenticationRequired;
                context.Response.Headers.ProxyAuthenticate = "Basic realm=\"proxy\"";
                return;
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync($"<p>Proxied {context.Request.GetDisplayUrl()}</p>");
        };
        return proxy;
    }

    /// <summary>Starts the real runner, which launches a local headless Chromium for each Script, and calls it through Lookout's client.</summary>
    private async Task WithRealRunner(Func<HttpRunnerClient, CancellationToken, Task> test)
    {
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(RepositoryRoot.Find(), "src/runner/index.ts"));
        start.Environment["PORT"] = "0";
        start.Environment["HEADLESS"] = "true";
        // A developer may have set these for running the runner themselves; this one takes requests without a token,
        // and launches its own Chromium rather than connecting to a Browserless.
        start.Environment.Remove("RUNNER_HOST");
        start.Environment.Remove("RUNNER_TOKEN");
        start.Environment.Remove("BROWSERLESS_URL");
        using var process = Process.Start(start)!;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.IsNotNull(line);
            var address = new Uri(line["Runner listening on ".Length..]);
            using var http = new HttpClient { BaseAddress = address };
            await test(new HttpRunnerClient(http, TimeProvider.System), timeout.Token);
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
