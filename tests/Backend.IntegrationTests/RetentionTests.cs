using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Running;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class RetentionTests(TestContext testContext)
{
    [TestMethod]
    public async Task PrunesRunsOlderThanThirtyDaysByDefault()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watchId = await CreatePausedWatch(client);
        var old = await RunNow(factory, client, watchId, RunOutcome.Errored);
        Elapse(factory, TimeSpan.FromDays(20));
        var recent = await RunNow(factory, client, watchId, RunOutcome.KnownFailure);
        Elapse(factory, TimeSpan.FromDays(10) - TimeSpan.FromHours(1));
        await Settle();
        Assert.AreSequenceEqual([recent, old], await History(client, watchId));

        Elapse(factory, TimeSpan.FromHours(2));

        await WaitForHistory(client, watchId, [recent]);
    }

    [TestMethod]
    public async Task PrunesRunsOlderThanTheRetentionPeriodSetInSettings()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        (await client.PutAsJsonAsync("/api/settings", new { retentionDays = 7 }, testContext.CancellationToken))
            .EnsureSuccessStatusCode();
        var watchId = await CreatePausedWatch(client);
        var old = await RunNow(factory, client, watchId, RunOutcome.Succeeded);
        Elapse(factory, TimeSpan.FromDays(2));
        var recent = await RunNow(factory, client, watchId, RunOutcome.Errored);
        Elapse(factory, TimeSpan.FromDays(5) - TimeSpan.FromHours(1));
        await Settle();
        Assert.AreSequenceEqual([recent, old], await History(client, watchId));

        Elapse(factory, TimeSpan.FromHours(2));

        await WaitForHistory(client, watchId, [recent]);
    }

    [TestMethod]
    public async Task NeverPrunesChanges()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watchId = await CreatePausedWatch(client);
        await RunNow(factory, client, watchId, RunOutcome.Succeeded, "£20");
        var change = await RunNow(factory, client, watchId, RunOutcome.Succeeded, "£19");
        await RunNow(factory, client, watchId, RunOutcome.Succeeded, "£19");
        await RunNow(factory, client, watchId, RunOutcome.Succeeded, "£18");
        // The newest Run is a Change too, so no other Run is kept as the latest.
        var latestChange = await RunNow(factory, client, watchId, RunOutcome.Succeeded, "£17");

        Elapse(factory, TimeSpan.FromDays(31));
        await WaitForHistory(client, watchId, [latestChange, latestChange - 1, change]);
        Elapse(factory, TimeSpan.FromDays(300));
        await Settle();

        Assert.AreSequenceEqual([latestChange, latestChange - 1, change], await History(client, watchId));
        var watch = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{watchId}", testContext.CancellationToken);
        Assert.AreEqual("£17", watch!["baseline"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task KeepsEachWatchsMostRecentRunHoweverOld()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watchId = await CreatePausedWatch(client);
        var otherWatchId = await CreatePausedWatch(client);
        await RunNow(factory, client, watchId, RunOutcome.Succeeded);
        var latest = await RunNow(factory, client, watchId, RunOutcome.Errored);
        var otherLatest = await RunNow(factory, client, otherWatchId, RunOutcome.Succeeded);

        Elapse(factory, TimeSpan.FromDays(31));

        await WaitForHistory(client, watchId, [latest]);
        Assert.AreSequenceEqual([otherLatest], await History(client, otherWatchId));
        var watch = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{watchId}", testContext.CancellationToken);
        Assert.AreEqual(latest, watch!["lastRun"]!["id"]!.GetValue<long>());
        Assert.AreEqual("errored", watch["lastRun"]!["outcome"]!.GetValue<string>());
    }

    private async Task<string> CreatePausedWatch(HttpClient client)
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
        var id = (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["id"]!.GetValue<string>();
        // Only the Runs each test starts itself are recorded.
        (await client.PostAsync(new Uri($"/api/watches/{id}/pause", UriKind.Relative), null, testContext.CancellationToken))
            .EnsureSuccessStatusCode();
        return id;
    }

    private async Task<long> RunNow(LookoutWebApplicationFactory factory, HttpClient client, string watchId,
        RunOutcome outcome, string content = "£20")
    {
        factory.Runner.Results.Enqueue(new RunnerResult
        {
            Outcome = outcome, Content = outcome == RunOutcome.Succeeded ? content : null, DurationMs = 1,
            ConsoleOutput = "Output\n"
        });
        var response = await client.PostAsync(new Uri($"/api/watches/{watchId}/runs", UriKind.Relative), null, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["id"]!.GetValue<long>();
    }

    private async Task<long[]> History(HttpClient client, string watchId)
    {
        var history = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{watchId}/runs", testContext.CancellationToken);
        return history!.Select(run => run!["id"]!.GetValue<long>()).ToArray();
    }

    /// <summary>
    /// Lets time pass, ending with at least one prune. Advancing through days would fire the scheduler's frequent timer
    /// for every period in between, so the clock jumps to just before the end and advances the rest.
    /// </summary>
    private static void Elapse(LookoutWebApplicationFactory factory, TimeSpan duration)
    {
        var end = factory.Time.GetUtcNow() + duration;
        factory.Time.AdjustTime(end - RunRetention.PruneInterval);
        factory.Time.Advance(RunRetention.PruneInterval);
    }

    private Task Settle() => Task.Delay(300, testContext.CancellationToken);

    /// <summary>Waits for the background pruning to leave exactly these Runs, newest first.</summary>
    private async Task WaitForHistory(HttpClient client, string watchId, long[] expected)
    {
        var history = await History(client, watchId);
        for (var attempt = 0; attempt < 100 && !history.SequenceEqual(expected); attempt++)
        {
            await Task.Delay(50, testContext.CancellationToken);
            history = await History(client, watchId);
        }
        Assert.AreSequenceEqual(expected, history);
    }
}
