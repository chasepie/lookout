using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;
using ChasePie.Lookout.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        Assert.IsNull(response.Headers.Location);
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

    [TestMethod]
    public async Task RecordsTheRunAfterTheCallerDisconnects()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<RunnerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Runner.OnExecute = (_, token) => { started.TrySetResult(); return finished.Task.WaitAsync(token); };
        using var client = factory.CreateClient();
        var watch = await CreateWatch(client);
        var id = watch["id"]!.GetValue<string>();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        var pending = client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);
        await caller.CancelAsync();
        finished.SetResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);

        JsonArray? history = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            history = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/runs", testContext.CancellationToken);
            if (history!.Count == 1) break;
            await Task.Delay(50, testContext.CancellationToken);
        }
        Assert.IsNotNull(history);
        Assert.HasCount(1, history);
        Assert.AreEqual("succeeded", history[0]!["outcome"]!.GetValue<string>());
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.AreEqual("£20", watches![0]!["baseline"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task TrimsTheNotificationUrlBeforeStoringAndExecutingIt()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.OnExecute = (request, _) =>
        {
            Assert.AreEqual("https://example.com/widget", request.Context.NotificationUrl);
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = factory.CreateClient();
        var watch = await CreateWatch(client, " https://example.com/widget\n");
        Assert.AreEqual("https://example.com/widget", watch["notificationUrl"]!.GetValue<string>());
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.AreEqual("https://example.com/widget", watches![0]!["notificationUrl"]!.GetValue<string>());
        await RunNow(client, watch["id"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecordsInvalidSucceededResultsAsErrored(bool hasBaseline)
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();
        var watch = await CreateWatch(client);
        var id = watch["id"]!.GetValue<string>();
        if (hasBaseline)
        {
            factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
            await RunNow(client, id);
        }
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = null, DurationMs = 1 });

        var run = await RunNow(client, id);

        Assert.AreEqual("errored", run["outcome"]!.GetValue<string>());
        Assert.Contains("content", run["errorMessage"]!.GetValue<string>());
        Assert.IsFalse(run["isChange"]!.GetValue<bool>());
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.AreEqual(hasBaseline ? "£20" : null, watches![0]!["baseline"]?.GetValue<string>());
        Assert.AreEqual("errored", watches[0]!["lastRun"]!["outcome"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ExplainsWhyAWatchWithoutACurrentScriptCannotRun()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();
        var watch = await CreateWatch(client);
        var id = watch["id"]!.GetValue<Guid>();
        // Arrange an incomplete legacy row. Creation through the API is atomic and cannot produce one.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            await database.Watches.Where(item => item.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CurrentScriptVersionId, (Guid?)null), testContext.CancellationToken);
        }

        var response = await client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.AreEqual("This Watch has no current Script version.", problem!["detail"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task PagesRunHistoryNewestFirstWithoutLosingOlderRuns()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "latest", DurationMs = 1 };
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "first", DurationMs = 1 });
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "second", DurationMs = 1 });
        using var client = factory.CreateClient();
        var watch = await CreateWatch(client);
        var id = watch["id"]!.GetValue<string>();
        for (var index = 0; index < 52; index++) await RunNow(client, id);

        var page = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/runs", testContext.CancellationToken);

        Assert.IsNotNull(page);
        Assert.HasCount(50, page);
        var before = page[^1]!["id"]!.GetValue<long>();
        var older = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/runs?before={before}&limit=2", testContext.CancellationToken);
        Assert.IsNotNull(older);
        Assert.HasCount(2, older);
        Assert.AreEqual("second", older[0]!["content"]!.GetValue<string>());
        Assert.AreEqual("first", older[1]!["content"]!.GetValue<string>());
        var limited = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/runs?limit=2", testContext.CancellationToken);
        Assert.HasCount(2, limited!);
        var invalid = await client.GetAsync(new Uri($"/api/watches/{id}/runs?limit=101", UriKind.Relative), testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [TestMethod]
    public async Task DeletingAWatchDeletesItsScriptVersionsAndRuns()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 };
        using var client = factory.CreateClient();
        var watch = await CreateWatch(client);
        var id = watch["id"]!.GetValue<Guid>();
        await RunNow(client, id.ToString());
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();

        // A set-based delete leaves the cascades to SQLite, which picks the order it removes dependents in.
        await database.Watches.Where(item => item.Id == id).ExecuteDeleteAsync(testContext.CancellationToken);

        Assert.IsFalse(await database.ScriptVersions.AnyAsync(testContext.CancellationToken));
        Assert.IsFalse(await database.Runs.AnyAsync(testContext.CancellationToken));
    }

    [TestMethod]
    public async Task RejectsARunEvaluatedAgainstABaselineAnotherRunAdvanced()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();
        var watch = await CreateWatch(client);
        var id = watch["id"]!.GetValue<Guid>();
        factory.Runner.OnExecute = async (_, cancellationToken) =>
        {
            // Another process records a Run of this Watch while this one is still executing.
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Watches.Where(item => item.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Baseline, "£19")
                    .SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);
            return new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 };
        };

        var response = await client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var history = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/runs", testContext.CancellationToken);
        Assert.IsEmpty(history!);
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.AreEqual("£19", watches![0]!["baseline"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task RecordsAFinishedRunWhenShutdownStartsBeforeItIsSaved()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.OnExecute = (_, _) =>
        {
            factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = factory.CreateClient();
        var watch = await CreateWatch(client);
        var id = watch["id"]!.GetValue<Guid>();
        // The test host disposes its services as it stops, so read the database file directly afterwards.
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Lookout");

        try
        {
            await client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // Unlike Kestrel, the test host can be disposed before it writes the response.
        }

        await using var database = new LookoutDbContext(new DbContextOptionsBuilder<LookoutDbContext>().UseSqlite(connectionString).Options);
        Assert.AreEqual(1, await database.Runs.CountAsync(run => run.WatchId == id, testContext.CancellationToken));
        Assert.AreEqual("£20", (await database.Watches.SingleAsync(testContext.CancellationToken)).Baseline);
    }

    [TestMethod]
    public async Task ValidatesTheNameAfterTrimmingIt()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();
        var name = new string('a', 195);

        var created = await CreateWatch(client, name: $"  {name}          ");

        Assert.AreEqual(name, created["name"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ListsWatchesAlphabeticallyIgnoringCase()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = factory.CreateClient();
        foreach (var name in (string[])["Zebra", "apple", "Banana"])
        {
            await CreateWatch(client, name: name);
        }

        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);

        Assert.AreSequenceEqual(["apple", "Banana", "Zebra"], watches!.Select(watch => watch!["name"]!.GetValue<string>()));
    }

    private async Task<JsonObject> CreateWatch(HttpClient client, string notificationUrl = "https://example.com/widget",
        string name = "Widget price")
    {
        using var form = WatchForm(notificationUrl, name);
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

    private static MultipartFormDataContent WatchForm(string notificationUrl = "https://example.com/widget",
        string name = "Widget price")
    {
        return new MultipartFormDataContent
        {
            { new StringContent(name), "name" },
            { new StringContent(notificationUrl), "notificationUrl" },
            { new StringContent("export default { async run() { return { success: true, content: '£20' }; } };"), "script", "price.ts" }
        };
    }
}
