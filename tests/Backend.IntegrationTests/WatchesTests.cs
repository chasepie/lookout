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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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
    public async Task ANewWatchIsPendingUntilItsFirstSucceededRun()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatch(client);
        Assert.AreEqual("pending", watch["status"]!.GetValue<string>());

        await RunNow(client, watch["id"]!.GetValue<string>());

        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.AreEqual("healthy", watches![0]!["status"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(RunOutcome.KnownFailure)]
    [DataRow(RunOutcome.Errored)]
    public async Task AHealthyWatchIsFailingFromAKnownFailureOrErroredRunUntilTheNextSucceededRun(RunOutcome outcome)
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = outcome, DurationMs = 1 });
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = outcome, DurationMs = 1 });
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client))["id"]!.GetValue<string>();
        await RunNow(client, id);

        await RunNow(client, id);
        var failing = await GetWatch(client, id);
        await RunNow(client, id);
        var stillFailing = await GetWatch(client, id);
        await RunNow(client, id);
        var recovered = await GetWatch(client, id);

        Assert.AreEqual("failing", failing["status"]!.GetValue<string>());
        Assert.AreEqual(1, failing["failureStreak"]!.GetValue<int>());
        Assert.AreEqual("failing", stillFailing["status"]!.GetValue<string>());
        Assert.AreEqual(2, stillFailing["failureStreak"]!.GetValue<int>());
        Assert.AreEqual("£20", stillFailing["baseline"]!.GetValue<string>());
        Assert.AreEqual("healthy", recovered["status"]!.GetValue<string>());
        Assert.AreEqual(0, recovered["failureStreak"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task AWatchWhoseFirstRunsFailIsFailingRatherThanPending()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Errored, ErrorMessage = "Selector missing", DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client))["id"]!.GetValue<string>();

        await RunNow(client, id);

        var watch = await GetWatch(client, id);
        Assert.AreEqual("failing", watch["status"]!.GetValue<string>());
        Assert.IsNull(watch["baseline"]);
    }

    [TestMethod]
    public async Task PausingKeepsTheBaselineAndResumingStartsAFreshFailureStreak()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.KnownFailure, DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client))["id"]!.GetValue<string>();
        await RunNow(client, id);
        await RunNow(client, id);

        var paused = await PostWatchAction(client, id, "pause");
        var resumed = await PostWatchAction(client, id, "resume");

        Assert.AreEqual("paused", paused["status"]!.GetValue<string>());
        Assert.IsTrue(paused["isPaused"]!.GetValue<bool>());
        Assert.AreEqual("£20", paused["baseline"]!.GetValue<string>());
        Assert.AreEqual("healthy", resumed["status"]!.GetValue<string>());
        Assert.IsFalse(resumed["isPaused"]!.GetValue<bool>());
        Assert.AreEqual(0, resumed["failureStreak"]!.GetValue<int>());
        Assert.AreEqual("£20", resumed["baseline"]!.GetValue<string>());
        Assert.AreEqual(resumed.ToJsonString(), (await GetWatch(client, id)).ToJsonString());
    }

    [TestMethod]
    public async Task APausedWatchCanStillRunNowAndStaysPaused()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Errored, DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client))["id"]!.GetValue<string>();
        await PostWatchAction(client, id, "pause");

        await RunNow(client, id);

        var watch = await GetWatch(client, id);
        Assert.AreEqual("paused", watch["status"]!.GetValue<string>());
        Assert.AreEqual(1, watch["failureStreak"]!.GetValue<int>());
        Assert.AreEqual("errored", watch["lastRun"]!["outcome"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ARunThatStartedBeforeResumingDoesNotContinueTheOldStreak()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Errored, DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client))["id"]!.GetValue<string>();
        await RunNow(client, id);
        await PostWatchAction(client, id, "pause");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<RunnerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Runner.OnExecute = (_, _) => { started.TrySetResult(); return finished.Task; };
        var pending = client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);

        await PostWatchAction(client, id, "resume");
        finished.SetResult(new RunnerResult { Outcome = RunOutcome.Errored, DurationMs = 1 });
        var response = await pending;

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var watch = await GetWatch(client, id);
        Assert.AreEqual(0, watch["failureStreak"]!.GetValue<int>());
        Assert.HasCount(1, (await client.GetFromJsonAsync<JsonArray>($"/api/watches/{id}/runs", testContext.CancellationToken))!);
    }

    [TestMethod]
    [DataRow("pause")]
    [DataRow("resume")]
    public async Task PausingOrResumingAMissingWatchIsNotFound(string action)
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var response = await client.PostAsync(new Uri($"/api/watches/{Guid.NewGuid()}/{action}", UriKind.Relative), null,
            testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task EditsAWatchsSettingsAndRunsWithThem()
    {
        await using var factory = new LookoutWebApplicationFactory();
        RunnerRequest? executed = null;
        factory.Runner.OnExecute = (request, _) =>
        {
            executed = request;
            return Task.FromResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var created = await CreateWatch(client);
        var id = created["id"]!.GetValue<string>();
        Assert.AreEqual(120, created["timeoutSeconds"]!.GetValue<int>());

        var response = await client.PutAsJsonAsync($"/api/watches/{id}",
            new { name = " Gadget price ", notificationUrl = " https://example.com/gadget ", timeoutSeconds = 30 },
            testContext.CancellationToken);
        await RunNow(client, id);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var edited = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.AreEqual("Gadget price", edited!["name"]!.GetValue<string>());
        Assert.AreEqual("https://example.com/gadget", edited["notificationUrl"]!.GetValue<string>());
        Assert.AreEqual(30, edited["timeoutSeconds"]!.GetValue<int>());
        Assert.AreEqual("Gadget price", (await GetWatch(client, id))["name"]!.GetValue<string>());
        Assert.AreEqual("https://example.com/gadget", executed!.Context.NotificationUrl);
        Assert.AreEqual(30_000, executed.TimeoutMs);
    }

    [TestMethod]
    [DataRow(" ", "https://example.com", 120)]
    [DataRow("Widget", "ftp://example.com", 120)]
    [DataRow("Widget", "https://example.com", 0)]
    [DataRow("Widget", "https://example.com", 601)]
    public async Task RejectsInvalidSettingsWithoutChangingTheWatch(string name, string notificationUrl, int timeoutSeconds)
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var created = await CreateWatch(client);
        var id = created["id"]!.GetValue<string>();

        var response = await client.PutAsJsonAsync($"/api/watches/{id}", new { name, notificationUrl, timeoutSeconds },
            testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(created.ToJsonString(), (await GetWatch(client, id)).ToJsonString());
    }

    [TestMethod]
    public async Task EditingAMissingWatchIsNotFound()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var response = await client.PutAsJsonAsync($"/api/watches/{Guid.NewGuid()}",
            new { name = "Widget", notificationUrl = "https://example.com", timeoutSeconds = 120 }, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task GetsOneWatchOrNotFound()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var created = await CreateWatch(client);

        var watch = await GetWatch(client, created["id"]!.GetValue<string>());
        var missing = await client.GetAsync(new Uri($"/api/watches/{Guid.NewGuid()}", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(created.ToJsonString(), watch.ToJsonString());
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task RejectsOverlappingRunsOfTheSameWatchAndAllowsAnotherAfterCompletion()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<RunnerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Runner.OnExecute = (_, _) => { started.TrySetResult(); return finished.Task; };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(name), "name" },
            { new StringContent(url), "notificationUrl" },
            { new StringContent(source), "script", fileName },
            { new StringContent("interval"), "scheduleKind" },
            { new StringContent("1"), "every" },
            { new StringContent("hours"), "unit" }
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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client))["id"]!.GetValue<Guid>();
        var other = (await CreateWatch(client, name: "Other"))["id"]!.GetValue<string>();
        await RunNow(client, id.ToString());
        await RunNow(client, other);
        // Arrange a second Script version directly; uploading one through the API comes with Script versions.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            var version = new ScriptVersion { WatchId = id, Source = "export default {}", FileKind = "ts" };
            database.ScriptVersions.Add(version);
            await database.SaveChangesAsync(testContext.CancellationToken);
            await database.Watches.Where(item => item.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CurrentScriptVersionId, version.Id), testContext.CancellationToken);
        }
        await RunNow(client, id.ToString());

        var response = await client.DeleteAsync(new Uri($"/api/watches/{id}", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        var missing = await client.GetAsync(new Uri($"/api/watches/{id}", UriKind.Relative), testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        var history = await client.GetAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, history.StatusCode);
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.AreSequenceEqual([other], watches!.Select(watch => watch!["id"]!.GetValue<string>()));
        Assert.HasCount(1, (await client.GetFromJsonAsync<JsonArray>($"/api/watches/{other}/runs", testContext.CancellationToken))!);
        // Script versions have no API to read them through yet.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
            Assert.IsFalse(await database.ScriptVersions.AnyAsync(version => version.WatchId == id, testContext.CancellationToken));
            Assert.IsFalse(await database.Runs.AnyAsync(run => run.WatchId == id, testContext.CancellationToken));
        }
    }

    [TestMethod]
    public async Task DeletingAMissingWatchIsNotFound()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var response = await client.DeleteAsync(new Uri($"/api/watches/{Guid.NewGuid()}", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ARunOfAWatchDeletedWhileItRanIsNotRecorded()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<RunnerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Runner.OnExecute = (_, _) => { started.TrySetResult(); return finished.Task; };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = (await CreateWatch(client))["id"]!.GetValue<string>();
        var pending = client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);

        var deleted = await client.DeleteAsync(new Uri($"/api/watches/{id}", UriKind.Relative), testContext.CancellationToken);
        finished.SetResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        var response = await pending;

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.IsFalse(await scope.ServiceProvider.GetRequiredService<LookoutDbContext>().Runs.AnyAsync(testContext.CancellationToken));
    }

    [TestMethod]
    public async Task RejectsARunEvaluatedAgainstABaselineAnotherRunAdvanced()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var name = new string('a', 195);

        var created = await CreateWatch(client, name: $"  {name}          ");

        Assert.AreEqual(name, created["name"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ListsWatchesAlphabeticallyIgnoringCase()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
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

    private async Task<JsonObject> GetWatch(HttpClient client, string id)
    {
        return (await client.GetFromJsonAsync<JsonObject>($"/api/watches/{id}", testContext.CancellationToken))!;
    }

    private async Task<JsonObject> PostWatchAction(HttpClient client, string id, string action)
    {
        var response = await client.PostAsync(new Uri($"/api/watches/{id}/{action}", UriKind.Relative), null, testContext.CancellationToken);
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
            { new StringContent("export default { async run() { return { success: true, content: '£20' }; } };"), "script", "price.ts" },
            { new StringContent("interval"), "scheduleKind" },
            { new StringContent("1"), "every" },
            { new StringContent("hours"), "unit" }
        };
    }
}
