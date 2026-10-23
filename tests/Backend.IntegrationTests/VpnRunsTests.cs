using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;
using ChasePie.Lookout.Model;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class VpnRunsTests(TestContext testContext)
{
    [TestMethod]
    public async Task AVpnRunGoesThroughTheProxyAndRecordsItsRegionAndExitIp()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888", "lookout", "secret"), "203.0.113.7", "New York");
        RunnerRequest? executed = null;
        factory.Runner.OnExecute = (request, _) =>
        {
            executed = request;
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        var run = await RunNow(factory, watch);

        Assert.IsTrue(watch["usesVpn"]!.GetValue<bool>());
        Assert.AreEqual(new ProxySettings("http://gluetun:8888", "lookout", "secret"), executed!.Proxy);
        Assert.AreEqual(RunOutcome.Succeeded, run.Outcome);
        Assert.AreEqual("New York", run.VpnRegion);
        Assert.AreEqual("203.0.113.7", run.VpnExitIp);
    }

    [TestMethod]
    public async Task ADirectRunDoesNotUseTheProxyOrRecordAnExit()
    {
        await using var factory = new LookoutWebApplicationFactory();
        RunnerRequest? executed = null;
        factory.Runner.OnExecute = (request, _) =>
        {
            executed = request;
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: false);

        var run = await RunNow(factory, watch);

        Assert.IsFalse(watch["usesVpn"]!.GetValue<bool>());
        Assert.IsNull(executed!.Proxy);
        Assert.AreEqual(RunOutcome.Succeeded, run.Outcome);
        Assert.IsNull(run.VpnRegion);
        Assert.IsNull(run.VpnExitIp);
    }

    [TestMethod]
    public async Task AVpnRunIsRefusedAsErroredAtOnceWithoutAVpn()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Vpn.Status = VpnStatus.Disabled;
        var executions = 0;
        factory.Runner.OnExecute = (_, _) =>
        {
            executions++;
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        var run = await RunNow(factory, watch);

        Assert.AreEqual(0, executions);
        Assert.AreEqual(RunOutcome.Errored, run.Outcome);
        Assert.AreEqual("No VPN is configured, so this VPN Run was not started.", run.ErrorMessage);
        Assert.IsNull(run.VpnRegion);
        Assert.IsNull(run.VpnExitIp);
    }

    [TestMethod]
    public async Task AVpnRunWaitsForTheVpnToConnect()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Vpn.Status = VpnStatus.Disconnected();
        RunnerRequest? executed = null;
        factory.Runner.OnExecute = (request, _) =>
        {
            executed = request;
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        var readsBefore = factory.Vpn.StatusReads;
        var running = RunNow(factory, watch);
        await AdvanceUntil(factory, () => factory.Vpn.StatusReads - readsBefore >= 2);
        factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "203.0.113.7", "New York");
        var run = await AdvanceUntilDone(factory, running);

        Assert.AreEqual(RunOutcome.Succeeded, run.Outcome);
        Assert.AreEqual(new ProxySettings("http://gluetun:8888"), executed!.Proxy);
        Assert.AreEqual("203.0.113.7", run.VpnExitIp);
    }

    [TestMethod]
    public async Task AVpnRunIsRefusedAsErroredWhenTheVpnDoesNotConnectWithinTwoMinutes()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var startedAt = factory.Time.GetUtcNow();
        var executions = 0;
        factory.Runner.OnExecute = (_, _) =>
        {
            executions++;
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);
        // Past the check at startup, which would otherwise rotate the VPN for not having connected
        await AdvanceUntil(factory, () => factory.Time.GetUtcNow() - startedAt > VpnRotation.StartupWait);
        factory.Vpn.Status = VpnStatus.Disconnected("gluetun's control server returned HTTP 500.");

        var run = await AdvanceUntilDone(factory, RunNow(factory, watch));

        Assert.AreEqual(0, executions);
        Assert.AreEqual(RunOutcome.Errored, run.Outcome);
        Assert.AreEqual(
            "The VPN is not connected, so this VPN Run was not started. gluetun's control server returned HTTP 500.",
            run.ErrorMessage);
        // Fake time moves on in steps of a second while the Run records itself, so it may finish a step or two later
        var waited = run.FinishedAt - run.StartedAt;
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMinutes(2), waited);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5), waited);
        Assert.IsNull(run.VpnExitIp);
    }

    [TestMethod]
    public async Task AVpnRunIsRefusedAtOnceWhenTheVpnIsMisconfigured()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Vpn.Status = VpnStatus.MisconfiguredBecause("gluetun refused Lookout's API key (HTTP 401).");
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        var readsBefore = factory.Vpn.StatusReads;

        // Without fake time moving on, a Run that waited for the VPN would never finish
        var run = await RunNow(factory, watch)
            .WaitAsync(TimeSpan.FromSeconds(10), testContext.CancellationToken);

        Assert.AreEqual(1, factory.Vpn.StatusReads - readsBefore);
        Assert.AreEqual(RunOutcome.Errored, run.Outcome);
        Assert.AreEqual(
            "The VPN is not connected, so this VPN Run was not started. gluetun refused Lookout's API key (HTTP 401).",
            run.ErrorMessage);
    }

    [TestMethod]
    public async Task AVpnWatchsTestRunGoesThroughTheProxy()
    {
        await using var factory = new LookoutWebApplicationFactory();
        RunnerRequest? executed = null;
        factory.Runner.OnExecute = (request, _) =>
        {
            executed = request;
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);

        var testRun = await TestRun(factory, watch);

        Assert.AreEqual(RunOutcome.Succeeded, testRun.Outcome);
        Assert.AreEqual(new ProxySettings("http://gluetun:8888"), executed!.Proxy);
    }

    [TestMethod]
    [DataRow(false, "No VPN is configured, so this Test run was not started.")]
    [DataRow(true, "The VPN is not connected, so this Test run was not started.")]
    public async Task AVpnWatchsTestRunIsRefusedAtOnceUnlessTheVpnIsConnected(bool enabled, string errorMessage)
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Vpn.Status = enabled ? VpnStatus.Disconnected() : VpnStatus.Disabled;
        var executions = 0;
        factory.Runner.OnExecute = (_, _) =>
        {
            executions++;
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: true);
        var readsBefore = factory.Vpn.StatusReads;

        var testRun = await TestRun(factory, watch);

        Assert.AreEqual(0, executions);
        Assert.AreEqual(1, factory.Vpn.StatusReads - readsBefore);
        Assert.AreEqual(RunOutcome.Errored, testRun.Outcome);
        Assert.AreEqual(errorMessage, testRun.ErrorMessage);
    }

    [TestMethod]
    public async Task ADirectWatchsTestRunDoesNotUseTheProxy()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Vpn.Status = VpnStatus.Disconnected();
        RunnerRequest? executed = null;
        factory.Runner.OnExecute = (request, _) =>
        {
            executed = request;
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client, usesVpn: false);

        var testRun = await TestRun(factory, watch);

        Assert.AreEqual(RunOutcome.Succeeded, testRun.Outcome);
        Assert.IsNull(executed!.Proxy);
    }

    private async Task<TestRun> TestRun(LookoutWebApplicationFactory factory, JsonObject watch) =>
        (await factory.Services.GetRequiredService<Runs>().TestAsync(Guid.Parse(watch["id"]!.GetValue<string>()),
            "export default { async run() { return { success: true, content: '£21' }; } };", "ts", testContext.CancellationToken))!;

    /// <summary>Moves fake time on in small steps, letting the Run react to each, until <paramref name="done"/> holds.</summary>
    private async Task AdvanceUntil(LookoutWebApplicationFactory factory, Func<bool> done)
    {
        for (var step = 0; !done(); step++)
        {
            Assert.IsLessThan(1000, step, "The Run never got there.");
            factory.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, testContext.CancellationToken);
        }
    }

    private async Task<T> AdvanceUntilDone<T>(LookoutWebApplicationFactory factory, Task<T> running)
    {
        await AdvanceUntil(factory, () => running.IsCompleted);
        return await running;
    }

    [TestMethod]
    public async Task EditingAWatchTurnsItsVpnOnAndOff()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var executed = new List<RunnerRequest>();
        factory.Runner.OnExecute = (request, _) =>
        {
            executed.Add(request);
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, usesVpn: false))["id"]!.GetValue<string>();

        await Edit(client, id, usesVpn: true);
        var vpnWatch = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{id}", testContext.CancellationToken);
        await RunNow(factory, id);
        await Edit(client, id, usesVpn: false);
        var directWatch = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{id}", testContext.CancellationToken);
        await RunNow(factory, id);

        Assert.IsTrue(vpnWatch!["usesVpn"]!.GetValue<bool>());
        Assert.IsFalse(directWatch!["usesVpn"]!.GetValue<bool>());
        Assert.AreEqual(new ProxySettings("http://gluetun:8888"), executed[0].Proxy);
        Assert.IsNull(executed[1].Proxy);
    }

    [TestMethod]
    public async Task EditingAWatchWithoutItsVpnSettingIsRejected()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client, usesVpn: true))["id"]!.GetValue<string>();

        var response = await client.PutAsJsonAsync($"/api/watches/{id}",
            new { name = "Widget price", notificationUrl = "https://example.com/widget", timeoutSeconds = 120 },
            testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var watch = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{id}", testContext.CancellationToken);
        Assert.IsTrue(watch!["usesVpn"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ReportsTheVpnStatusWithoutItsProxySettings()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        factory.Vpn.Status = VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888", "lookout", "secret"), "203.0.113.7", "New York");
        var connected = await client.GetFromJsonAsync<JsonObject>("/api/vpn", testContext.CancellationToken);
        factory.Vpn.Status = VpnStatus.Disconnected("gluetun refused the API key (HTTP 401).");
        var disconnected = await client.GetFromJsonAsync<JsonObject>("/api/vpn", testContext.CancellationToken);

        Assert.AreEqual(
            """{"enabled":true,"connected":true,"rotating":false,"region":"New York","exitIp":"203.0.113.7","error":null}""",
            connected!.ToJsonString());
        Assert.AreEqual(
            """{"enabled":true,"connected":false,"rotating":false,"region":null,"exitIp":null,"error":"gluetun refused the API key (HTTP 401)."}""",
            disconnected!.ToJsonString());
    }

    [TestMethod]
    public async Task TheVpnStatusNeedsALogin()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/vpn", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task Edit(HttpClient client, string id, bool usesVpn)
    {
        var response = await client.PutAsJsonAsync($"/api/watches/{id}",
            new { name = "Widget price", notificationUrl = "https://example.com/widget", timeoutSeconds = 120, usesVpn },
            testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<JsonObject> CreateWatch(HttpClient client, bool usesVpn)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Widget price"), "name" },
            { new StringContent("https://example.com/widget"), "notificationUrl" },
            { new StringContent("export default { async run() { return { success: true, content: '£20' }; } };"), "script", "price.ts" },
            { new StringContent("interval"), "scheduleKind" },
            { new StringContent("1"), "every" },
            { new StringContent("hours"), "unit" },
            { new StringContent(usesVpn ? "true" : "false"), "usesVpn" }
        };
        var response = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
    }

    private static Task<Run> RunNow(LookoutWebApplicationFactory factory, JsonObject watch) =>
        RunNow(factory, watch["id"]!.GetValue<string>());

    private static async Task<Run> RunNow(LookoutWebApplicationFactory factory, string id)
    {
        var admitted = Assert.IsInstanceOfType<Admitted>(factory.Services.GetRequiredService<Runs>().Run(Guid.Parse(id)));
        var execution = await admitted.Finished;
        Assert.AreEqual(RunExecutionStatus.Recorded, execution.Status);
        return execution.Run!;
    }
}
