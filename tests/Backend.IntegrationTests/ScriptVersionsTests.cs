using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class ScriptVersionsTests(TestContext testContext)
{
    private const string NewScript = "export default { async run() { return { success: true, content: '£20 (new)' }; } };";

    [TestMethod]
    public async Task ANewVersionResetsTheBaselineSoTheWatchIsPendingAndItsNextRunIsSilent()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "20.00 GBP", DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = await CreateWatch(client);
        await RunNow(client, id);

        var version = await UploadScript(client, id, NewScript);

        var watch = await GetWatch(client, id);
        Assert.AreEqual(version["id"]!.GetValue<string>(), watch["currentScriptVersionId"]!.GetValue<string>());
        Assert.IsNull(watch["baseline"]);
        Assert.AreEqual("pending", watch["status"]!.GetValue<string>());
        var run = await RunNow(client, id);
        Assert.IsFalse(run["isChange"]!.GetValue<bool>());
        Assert.AreEqual("20.00 GBP", (await GetWatch(client, id))["baseline"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task KeepingTheBaselineLeavesTheBaselineAndStatusSoTheNextRunIsCompared()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Errored, ErrorMessage = "No price", DurationMs = 1 });
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£19", DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = await CreateWatch(client);
        await RunNow(client, id);
        await RunNow(client, id);

        await UploadScript(client, id, NewScript, keepBaseline: true);

        var watch = await GetWatch(client, id);
        Assert.AreEqual("£20", watch["baseline"]!.GetValue<string>());
        Assert.AreEqual("failing", watch["status"]!.GetValue<string>());
        Assert.AreEqual(1, watch["failureStreak"]!.GetValue<int>());
        var run = await RunNow(client, id);
        Assert.IsTrue(run["isChange"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ListsScriptVersionsNewestFirstAndEachRunRecordsTheVersionItRan()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = await CreateWatch(client);
        await RunNow(client, id);
        factory.Time.Advance(TimeSpan.FromMinutes(30));

        await UploadScript(client, id, NewScript, "price.js");
        await RunNow(client, id);

        var versions = (await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/script-versions", testContext.CancellationToken))!;
        Assert.HasCount(2, versions);
        Assert.AreEqual(2, versions[0]!["number"]!.GetValue<int>());
        Assert.AreEqual("js", versions[0]!["fileKind"]!.GetValue<string>());
        Assert.AreEqual(new DateTimeOffset(2026, 10, 19, 12, 30, 0, TimeSpan.Zero), versions[0]!["uploadedAt"]!.GetValue<DateTimeOffset>());
        Assert.IsTrue(versions[0]!["isCurrent"]!.GetValue<bool>());
        Assert.AreEqual(1, versions[1]!["number"]!.GetValue<int>());
        Assert.AreEqual("ts", versions[1]!["fileKind"]!.GetValue<string>());
        Assert.AreEqual(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), versions[1]!["uploadedAt"]!.GetValue<DateTimeOffset>());
        Assert.IsFalse(versions[1]!["isCurrent"]!.GetValue<bool>());
        var history = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/runs", testContext.CancellationToken);
        Assert.AreSequenceEqual(
            versions.Select(version => version!["id"]!.GetValue<string>()),
            history!.Select(run => run!["scriptVersionId"]!.GetValue<string>()));
    }

    [TestMethod]
    public async Task ShowsTheSourceOfEachVersionOnlyThroughItsOwnWatch()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = await CreateWatch(client);
        var otherId = await CreateWatch(client);
        var original = (await GetWatch(client, id))["currentScriptVersionId"]!.GetValue<string>();
        var uploaded = (await UploadScript(client, id, NewScript))["id"]!.GetValue<string>();

        var originalVersion = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{id}/script-versions/{original}", testContext.CancellationToken);
        var uploadedVersion = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{id}/script-versions/{uploaded}", testContext.CancellationToken);
        var throughOtherWatch = await client.GetAsync(new Uri($"/api/watches/{otherId}/script-versions/{uploaded}", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual("export default { async run() { return { success: true, content: '£20' }; } };", originalVersion!["source"]!.GetValue<string>());
        Assert.AreEqual(1, originalVersion["number"]!.GetValue<int>());
        Assert.IsFalse(originalVersion["isCurrent"]!.GetValue<bool>());
        Assert.AreEqual(NewScript, uploadedVersion!["source"]!.GetValue<string>());
        Assert.IsTrue(uploadedVersion["isCurrent"]!.GetValue<bool>());
        Assert.AreEqual(HttpStatusCode.NotFound, throughOtherWatch.StatusCode);
    }

    [TestMethod]
    public async Task UploadingToTestRunningOrListingTheVersionsOfAMissingWatchIsNotFound()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var missing = Guid.NewGuid().ToString();

        using var upload = await PostScript(client, missing, NewScript);
        using var testRun = await PostTestRun(client, missing, NewScript);
        using var list = await client.GetAsync(new Uri($"/api/watches/{missing}/script-versions", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, upload.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, testRun.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, list.StatusCode);
    }

    [TestMethod]
    [DataRow("price.py", NewScript)]
    [DataRow("price.ts", " \n ")]
    public async Task RejectsAnInvalidScriptWithoutChangingTheWatch(string fileName, string source)
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = await CreateWatch(client);
        await RunNow(client, id);
        var before = await GetWatch(client, id);

        using var response = await PostScript(client, id, source, fileName);
        using var testRun = await PostTestRun(client, id, source, fileName);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, testRun.StatusCode);
        var after = await GetWatch(client, id);
        Assert.AreEqual(before["currentScriptVersionId"]!.GetValue<string>(), after["currentScriptVersionId"]!.GetValue<string>());
        Assert.AreEqual("£20", after["baseline"]!.GetValue<string>());
        var versions = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/script-versions", testContext.CancellationToken);
        Assert.HasCount(1, versions!);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ARunOfAVersionReplacedWhileItRanIsNotRecorded(bool keepBaseline)
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = await CreateWatch(client);
        await RunNow(client, id);
        factory.Runner.OnExecute = async (_, _) =>
        {
            await UploadScript(client, id, NewScript, keepBaseline: keepBaseline);
            return new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£19", DurationMs = 1 };
        };

        using var response = await client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.AreEqual("A new Script version was uploaded while this Run was in progress, so this Run was not recorded.",
            problem!["detail"]!.GetValue<string>());
        var history = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/runs", testContext.CancellationToken);
        Assert.HasCount(1, history!);
        var watch = await GetWatch(client, id);
        Assert.AreEqual(keepBaseline ? "£20" : null, watch["baseline"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task ATestRunShowsTheResultWithoutTouchingTheWatchsHistoryBaselineStatusOrScript()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = await CreateWatch(client);
        await RunNow(client, id);
        var before = await GetWatch(client, id);
        RunnerRequest? executed = null;
        factory.Runner.OnExecute = (request, _) =>
        {
            executed = request;
            return Task.FromResult(new RunnerResult
            {
                Outcome = RunOutcome.Succeeded, Content = "£19", Details = "In stock", DurationMs = 42,
                Screenshot = "iVBORw0KGgo=", ConsoleOutput = "Found the price\n"
            });
        };

        using var response = await PostTestRun(client, id, NewScript, "price.js");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
        Assert.AreEqual("succeeded", result["outcome"]!.GetValue<string>());
        Assert.AreEqual("£19", result["content"]!.GetValue<string>());
        Assert.AreEqual("In stock", result["details"]!.GetValue<string>());
        Assert.AreEqual(42, result["durationMs"]!.GetValue<double>());
        Assert.AreEqual("iVBORw0KGgo=", result["screenshot"]!.GetValue<string>());
        Assert.AreEqual("Found the price\n", result["consoleOutput"]!.GetValue<string>());
        Assert.AreEqual(NewScript, executed!.Source);
        Assert.AreEqual("js", executed.FileKind);
        Assert.AreEqual("https://example.com/widget", executed.Context.NotificationUrl);
        Assert.AreEqual(120_000, executed.TimeoutMs);
        Assert.IsTrue(executed.AlwaysScreenshot);
        var after = await GetWatch(client, id);
        Assert.AreEqual(before.ToJsonString(), after.ToJsonString());
        var history = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/runs", testContext.CancellationToken);
        Assert.HasCount(1, history!);
        var versions = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/script-versions", testContext.CancellationToken);
        Assert.HasCount(1, versions!);
    }

    [TestMethod]
    public async Task ATestRunReportsASucceededResultWithoutContentAsARunWouldRecordIt()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = new RunnerResult { Outcome = RunOutcome.Succeeded, DurationMs = 1 };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = await CreateWatch(client);

        using var response = await PostTestRun(client, id, NewScript);

        var result = (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
        Assert.AreEqual("errored", result["outcome"]!.GetValue<string>());
        Assert.AreEqual("Succeeded runner result did not include content.", result["errorMessage"]!.GetValue<string>());
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
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["id"]!.GetValue<string>();
    }

    private async Task<JsonObject> UploadScript(HttpClient client, string id, string source, string fileName = "price.ts",
        bool? keepBaseline = null)
    {
        using var response = await PostScript(client, id, source, fileName, keepBaseline);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
    }

    private Task<HttpResponseMessage> PostScript(HttpClient client, string id, string source, string fileName = "price.ts",
        bool? keepBaseline = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(source), "script", fileName } };
        if (keepBaseline is { } keep)
        {
            form.Add(new StringContent(keep ? "true" : "false"), "keepBaseline");
        }
        return client.PostAsync(new Uri($"/api/watches/{id}/script-versions", UriKind.Relative), form, testContext.CancellationToken);
    }

    private Task<HttpResponseMessage> PostTestRun(HttpClient client, string id, string source, string fileName = "price.ts")
    {
        var form = new MultipartFormDataContent { { new StringContent(source), "script", fileName } };
        return client.PostAsync(new Uri($"/api/watches/{id}/test-runs", UriKind.Relative), form, testContext.CancellationToken);
    }

    private async Task<JsonObject> GetWatch(HttpClient client, string id)
    {
        return (await client.GetFromJsonAsync<JsonObject>($"/api/watches/{id}", testContext.CancellationToken))!;
    }

    private async Task<JsonObject> RunNow(HttpClient client, string id)
    {
        var response = await client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
    }
}
