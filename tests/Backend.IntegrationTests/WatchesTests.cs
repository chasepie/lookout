using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class WatchesTests(TestContext testContext)
{
    [TestMethod]
    public async Task CreatesAWatchWithAScriptAndListsItAsPending()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();
        using var form = WatchForm();

        var response = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.IsNotNull(created);
        Assert.IsNotNull(watches);
        Assert.HasCount(1, watches);
        Assert.AreEqual(created["id"]!.GetValue<string>(), watches[0]!["id"]!.GetValue<string>());
        Assert.AreEqual("Widget price", watches[0]!["name"]!.GetValue<string>());
        Assert.AreEqual("https://example.com/widget", watches[0]!["notificationUrl"]!.GetValue<string>());
        Assert.IsNotNull(watches[0]!["currentScriptVersionId"]);
        Assert.IsNull(watches[0]!["baseline"]);
        Assert.IsNull(watches[0]!["lastRun"]);
    }

    [TestMethod]
    public async Task FirstSucceededRunSetsTheBaselineWithoutAChange()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult
        {
            Outcome = RunOutcome.Succeeded, Content = "£20", Details = "In stock", DurationMs = 123
        });
        using var client = factory.CreateClient();
        var watch = await CreateWatch(client);

        var run = await RunNow(client, watch["id"]!.GetValue<string>());

        Assert.AreEqual("succeeded", run["outcome"]!.GetValue<string>());
        Assert.AreEqual("£20", run["content"]!.GetValue<string>());
        Assert.AreEqual("In stock", run["details"]!.GetValue<string>());
        Assert.AreEqual(123, run["durationMs"]!.GetValue<double>());
        Assert.AreEqual(watch["currentScriptVersionId"]!.GetValue<string>(), run["scriptVersionId"]!.GetValue<string>());
        Assert.IsFalse(run["isChange"]!.GetValue<bool>());
        Assert.IsGreaterThanOrEqualTo(run["startedAt"]!.GetValue<DateTimeOffset>(), run["finishedAt"]!.GetValue<DateTimeOffset>());
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.AreEqual("£20", watches![0]!["baseline"]!.GetValue<string>());
        Assert.AreEqual(run["id"]!.GetValue<long>(), watches[0]!["lastRun"]!["id"]!.GetValue<long>());
    }

    [TestMethod]
    [DataRow("£20", "£19", true)]
    [DataRow("£20", " \t£20\r\n", false)]
    [DataRow(" \n£20\t", "£20", false)]
    [DataRow("", " ", false)]
    [DataRow("", "£20", true)]
    [DataRow("£20", "", true)]
    [DataRow("In stock", "in stock", true)]
    [DataRow("a b", "a  b", true)]
    public async Task ComparesSucceededRunsUsingTrimmedContent(string baseline, string content, bool isChange)
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = baseline, DurationMs = 1 });
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = content, Details = "Different details", DurationMs = 2 });
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = content, DurationMs = 3 });
        using var client = factory.CreateClient();
        var watch = await CreateWatch(client);
        var id = watch["id"]!.GetValue<string>();
        await RunNow(client, id);

        var run = await RunNow(client, id);

        Assert.AreEqual(isChange, run["isChange"]!.GetValue<bool>());
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.AreEqual(content, watches![0]!["baseline"]!.GetValue<string>());
        var next = await RunNow(client, id);
        Assert.IsFalse(next["isChange"]!.GetValue<bool>());
    }

    [TestMethod]
    [DataRow(RunOutcome.KnownFailure, false)]
    [DataRow(RunOutcome.Errored, false)]
    [DataRow(RunOutcome.KnownFailure, true)]
    [DataRow(RunOutcome.Errored, true)]
    public async Task RecordsFailuresWithoutChangingTheBaseline(RunOutcome outcome, bool hasBaseline)
    {
        await using var factory = new LookoutWebApplicationFactory();
        if (hasBaseline)
        {
            factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        }
        factory.Runner.Results.Enqueue(new RunnerResult
        {
            Outcome = outcome, Content = "Access denied", Details = "Try later", ErrorMessage = "Page unavailable", DurationMs = 42
        });
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£19", DurationMs = 1 });
        using var client = factory.CreateClient();
        var watch = await CreateWatch(client);
        var id = watch["id"]!.GetValue<string>();
        if (hasBaseline)
        {
            await RunNow(client, id);
        }

        var failure = await RunNow(client, id);

        Assert.IsFalse(failure["isChange"]!.GetValue<bool>());
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.AreEqual(hasBaseline ? "£20" : null, watches![0]!["baseline"]?.GetValue<string>());
        Assert.AreEqual(hasBaseline ? "£20" : null, watches[0]!["latestContent"]?.GetValue<string>());
        var history = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/runs", testContext.CancellationToken);
        Assert.IsNotNull(history);
        Assert.HasCount(hasBaseline ? 2 : 1, history);
        Assert.AreEqual("Try later", history[0]!["details"]!.GetValue<string>());
        Assert.AreEqual("Page unavailable", history[0]!["errorMessage"]!.GetValue<string>());
        Assert.AreEqual(failure["outcome"]!.GetValue<string>(), history[0]!["outcome"]!.GetValue<string>());
        var recovered = await RunNow(client, id);
        Assert.AreEqual(hasBaseline, recovered["isChange"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task RejectsOverlappingRunsOfTheSameWatchAndAllowsAnotherAfterCompletion()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<RunnerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Runner.OnExecute = (_, _) => { started.TrySetResult(); return finished.Task; };
        using var client = factory.CreateClient();
        var watch = await CreateWatch(client);
        var id = watch["id"]!.GetValue<string>();
        var first = RunNow(client, id);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);
            var duplicate = await client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), testContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
        }
        finally
        {
            finished.TrySetResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
            await first;
        }
        factory.Runner.OnExecute = null;
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£19", DurationMs = 1 });
        var next = await RunNow(client, id);
        Assert.IsTrue(next["isChange"]!.GetValue<bool>());
    }

    [TestMethod]
    [DataRow("", "https://example.com", "price.ts", "export default {}")]
    [DataRow("  ", "https://example.com", "price.ts", "export default {}")]
    [DataRow("Widget", "javascript:alert(1)", "price.ts", "export default {}")]
    [DataRow("Widget", "not a URL", "price.ts", "export default {}")]
    [DataRow("Widget", "https://example.com", "price.txt", "export default {}")]
    [DataRow("Widget", "https://example.com", "price.ts", " ")]
    public async Task RejectsInvalidWatchUploadsWithoutCreatingAWatch(string name, string url, string fileName, string source)
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();
        using var form = new MultipartFormDataContent
        {
            { new StringContent(name), "name" },
            { new StringContent(url), "notificationUrl" },
            { new StringContent(source), "script", fileName }
        };
        var response = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.IsNotNull(watches);
        Assert.IsEmpty(watches);
    }

    private async Task<JsonObject> CreateWatch(HttpClient client)
    {
        using var form = WatchForm();
        var response = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
    }

    private async Task<JsonObject> RunNow(HttpClient client, string id)
    {
        var response = await client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
    }

    private static MultipartFormDataContent WatchForm()
    {
        return new MultipartFormDataContent
        {
            { new StringContent("Widget price"), "name" },
            { new StringContent("https://example.com/widget"), "notificationUrl" },
            { new StringContent("export default { async run() { return { success: true, content: '£20' }; } };"), "script", "price.ts" }
        };
    }
}
