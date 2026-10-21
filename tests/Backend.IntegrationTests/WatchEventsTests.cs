using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class WatchEventsTests(TestContext testContext)
{
    [TestMethod]
    public async Task PushesRunStartedRunFinishedAndStatusChangedInOrderForARun()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);
        var id = await CreateWatch(client);

        var run = await RunNow(client, id);

        var events = await listener.NextAsync(3, testContext.CancellationToken);
        Assert.AreEqual("RunStarted RunFinished WatchStatusChanged", Names(events));
        Assert.IsTrue(events.All(item => item.Payload.GetProperty("watchId").GetString() == id));
        Assert.AreEqual(run["id"]!.GetValue<long>(), events[1].Payload.GetProperty("runId").GetInt64());
        Assert.AreEqual("succeeded", events[1].Payload.GetProperty("outcome").GetString());
        Assert.AreEqual("healthy", events[2].Payload.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task PushesEventsForAScheduledRun()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = new RunnerResult { Outcome = RunOutcome.KnownFailure, Details = "Access denied", DurationMs = 1 };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);
        var id = await CreateWatch(client);

        factory.Time.Advance(TimeSpan.FromHours(1));

        var events = await listener.NextAsync(3, testContext.CancellationToken);
        Assert.AreEqual("RunStarted RunFinished WatchStatusChanged", Names(events));
        Assert.AreEqual(id, events[0].Payload.GetProperty("watchId").GetString());
        Assert.AreEqual("knownFailure", events[1].Payload.GetProperty("outcome").GetString());
        Assert.AreEqual("failing", events[2].Payload.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task PushesTheContentOnlyOfAChangeAndAStatusChangeOnlyWhenTheStatusMoves()
    {
        await using var factory = new LookoutWebApplicationFactory();
        foreach (var content in new[] { "£20", "£20", "£19" })
        {
            factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = content, DurationMs = 1 });
        }
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);
        var id = await CreateWatch(client);

        await RunNow(client, id);
        await RunNow(client, id);
        await RunNow(client, id);
        // Pausing pushes an event that shows nothing else came first.
        await PostWatchAction(client, id, "pause");

        var events = await listener.NextAsync(8, testContext.CancellationToken);
        Assert.AreEqual("RunStarted RunFinished WatchStatusChanged RunStarted RunFinished RunStarted RunFinished WatchStatusChanged", Names(events));
        var (first, unchanged, change) = (events[1].Payload, events[4].Payload, events[6].Payload);
        Assert.IsFalse(first.GetProperty("isChange").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, first.GetProperty("content").ValueKind);
        Assert.IsFalse(unchanged.GetProperty("isChange").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, unchanged.GetProperty("content").ValueKind);
        Assert.IsTrue(change.GetProperty("isChange").GetBoolean());
        Assert.AreEqual("£19", change.GetProperty("content").GetString());
        Assert.AreEqual("paused", events[7].Payload.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task AWatchPausedDuringARunStaysPausedWhenTheRunFinishes()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<RunnerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Runner.OnExecute = (_, _) => { started.TrySetResult(); return finished.Task; };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);
        var id = await CreateWatch(client);
        var other = await CreateWatch(client);

        var running = RunNow(client, id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);
        await PostWatchAction(client, id, "pause");
        finished.SetResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });
        await running;
        // Pausing another Watch pushes an event that shows everything before it came first.
        await PostWatchAction(client, other, "pause");

        var statuses = new List<string>();
        for (var next = await listener.NextAsync(testContext.CancellationToken);
             next.Payload.GetProperty("watchId").GetString() != other;
             next = await listener.NextAsync(testContext.CancellationToken))
        {
            if (next.Name == "WatchStatusChanged")
            {
                statuses.Add(next.Payload.GetProperty("status").GetString()!);
            }
        }
        Assert.AreEqual("paused", string.Join(' ', statuses));
        Assert.AreEqual("paused", (await GetWatch(client, id))["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task FinishesARunThatIsNotRecordedWithoutAnOutcome()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<RunnerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Runner.OnExecute = (_, _) => { started.TrySetResult(); return finished.Task; };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);
        var id = await CreateWatch(client);

        var running = client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);
        var deleted = await client.DeleteAsync(new Uri($"/api/watches/{id}", UriKind.Relative), testContext.CancellationToken);
        finished.SetResult(new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 });

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await running).StatusCode);
        var events = await listener.NextAsync(2, testContext.CancellationToken);
        Assert.AreEqual("RunStarted RunFinished", Names(events));
        Assert.AreEqual(id, events[1].Payload.GetProperty("watchId").GetString());
        Assert.AreEqual(JsonValueKind.Null, events[1].Payload.GetProperty("runId").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, events[1].Payload.GetProperty("outcome").ValueKind);
    }

    [TestMethod]
    public async Task PushesStatusChangesWhenAWatchIsPausedAndResumed()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);
        var id = await CreateWatch(client);

        await PostWatchAction(client, id, "pause");
        await PostWatchAction(client, id, "pause");
        await PostWatchAction(client, id, "resume");
        await PostWatchAction(client, id, "resume");
        await PostWatchAction(client, id, "pause");

        var events = await listener.NextAsync(3, testContext.CancellationToken);
        Assert.AreEqual("WatchStatusChanged WatchStatusChanged WatchStatusChanged", Names(events));
        Assert.IsTrue(events.All(item => item.Payload.GetProperty("watchId").GetString() == id));
        Assert.AreEqual("paused pending paused", string.Join(' ', events.Select(item => item.Payload.GetProperty("status").GetString())));
    }

    [TestMethod]
    public async Task AWatchIsRunningAlongsideItsStatusWhileARunIsInProgress()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(new RunnerResult { Outcome = RunOutcome.Errored, ErrorMessage = "Timed out", DurationMs = 1 });
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = await CreateWatch(client);
        await RunNow(client, id);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<RunnerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Runner.OnExecute = (_, _) => { started.TrySetResult(); return finished.Task; };

        var running = RunNow(client, id);
        JsonObject during;
        JsonArray listed;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);
            during = await GetWatch(client, id);
            listed = (await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken))!;
        }
        finally
        {
            finished.TrySetResult(new RunnerResult { Outcome = RunOutcome.Errored, ErrorMessage = "Timed out", DurationMs = 1 });
            await running;
        }
        var after = await GetWatch(client, id);

        Assert.IsTrue(during["isRunning"]!.GetValue<bool>());
        Assert.AreEqual("failing", during["status"]!.GetValue<string>());
        Assert.IsTrue(listed[0]!["isRunning"]!.GetValue<bool>());
        Assert.IsFalse(after["isRunning"]!.GetValue<bool>());
        Assert.AreEqual("failing", after["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task RejectsAConnectionWithoutALogin()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.CreateAsync(factory, logIn: false, testContext.CancellationToken);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => listener.StartAsync(cancellationToken: testContext.CancellationToken));

        Assert.AreEqual(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [TestMethod]
    public async Task RejectsAConnectionNegotiatedWithoutTheRequestToken()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.CreateAsync(factory, logIn: true, testContext.CancellationToken);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            listener.StartAsync(sendRequestToken: false, cancellationToken: testContext.CancellationToken));

        Assert.AreEqual(HttpStatusCode.BadRequest, error.StatusCode);
    }

    [TestMethod]
    public async Task RejectsAWebSocketThatSkipsNegotiation()
    {
        // A page on another port of the same host could open one with the user's cookies, which ignore ports.
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var listener = await WatchEventsListener.CreateAsync(factory, logIn: true, testContext.CancellationToken);

        var error = await Assert.ThrowsAsync<Exception>(() =>
            listener.StartAsync(skipNegotiation: true, cancellationToken: testContext.CancellationToken));

        Assert.Contains("status code: 400", error.ToString());
    }

    [TestMethod]
    public async Task LoggingOutClosesOnlyThatSessionsConnections()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.DefaultResult = new RunnerResult { Outcome = RunOutcome.Succeeded, Content = "£20", DurationMs = 1 };
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        await using var loggingOut = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);
        await using var staying = await WatchEventsListener.ConnectAsync(factory, testContext.CancellationToken);

        var logout = await loggingOut.Client.DeleteAsync(new Uri("/api/session", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        await loggingOut.Closed.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);
        await RunNow(client, await CreateWatch(client));
        Assert.AreEqual("RunStarted", (await staying.NextAsync(testContext.CancellationToken)).Name);
        Assert.IsFalse(staying.Closed.IsCompleted);
    }

    private static string Names(IEnumerable<WatchEvent> events) => string.Join(' ', events.Select(item => item.Name));

    private async Task<string> CreateWatch(HttpClient client, string notificationUrl = "https://example.com/widget")
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Widget price"), "name" },
            { new StringContent(notificationUrl), "notificationUrl" },
            { new StringContent("export default {}"), "script", "price.ts" },
            { new StringContent("interval"), "scheduleKind" },
            { new StringContent("1"), "every" },
            { new StringContent("hours"), "unit" }
        };
        var response = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["id"]!.GetValue<string>();
    }

    private async Task PostWatchAction(HttpClient client, string id, string action)
    {
        var response = await client.PostAsync(new Uri($"/api/watches/{id}/{action}", UriKind.Relative), null, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<JsonObject> GetWatch(HttpClient client, string id) =>
        (await client.GetFromJsonAsync<JsonObject>($"/api/watches/{id}", testContext.CancellationToken))!;

    private async Task<JsonObject> RunNow(HttpClient client, string id)
    {
        var response = await client.PostAsync(new Uri($"/api/watches/{id}/runs", UriKind.Relative), null, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!;
    }
}
