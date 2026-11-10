using System.Net;
using System.Net.Http.Json;
using ChasePie.Lookout.Backend.Controllers;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using ChasePie.Lookout.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// The notifications finished runs send, with the Runner and Pushover faked at their HTTP boundaries.
/// </summary>
[TestClass]
public sealed class RunNotificationsTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task AContentChangeIsSentAsTheSubscriptionAsks()
    {
        var runner = new FakeRunner(Success("Sold Out"));
        var pushover = new FakePushover();
        await using var factory = CreateFactory(runner, pushover);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var channel = await client.CreateChannelAsync("Phone", Ct);
        var watchId = await client.CreateWatchAsync("Shoe stock", Ct);
        await client.SubscribeAsync(watchId, Ct, new
        {
            NotificationChannelId = channel.Id,
            Events = new[] { WatchEvent.ContentChange },
            Pushover = new { Priority = 1, Sound = "cashregister" },
        });

        await client.RunAsync(watchId, Ct);
        runner.Response = Success("In Stock");
        await client.RunAsync(watchId, Ct);

        await pushover.WaitForRequestsAsync(1, Ct);
        var fields = pushover.Requests.Single().Fields;
        Assert.AreEqual(LookoutApi.ApplicationToken, fields["token"]);
        Assert.AreEqual(LookoutApi.UserKey, fields["user"]);
        Assert.AreEqual("Shoe stock", fields["title"]);
        Assert.AreEqual("Sold Out → In Stock", fields["message"]);
        Assert.AreEqual("1", fields["priority"]);
        Assert.AreEqual("cashregister", fields["sound"]);
    }

    [TestMethod]
    public async Task EachSubscriptionGetsOnlyItsEventsWithItsOwnOptions()
    {
        var runner = new FakeRunner(Success("Sold Out"));
        var pushover = new FakePushover();
        await using var factory = CreateFactory(runner, pushover);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var channel = await client.CreateChannelAsync("Phone", Ct);
        var watchId = await client.CreateWatchAsync("Shoe stock", Ct);
        await client.SubscribeAsync(watchId, Ct,
            new { NotificationChannelId = channel.Id, Events = new[] { WatchEvent.Crash }, Pushover = new { Priority = -1 } },
            new { NotificationChannelId = channel.Id, Events = new[] { WatchEvent.Recovery }, Pushover = new { Priority = 1 } });

        await client.RunAsync(watchId, Ct);
        runner.Response = Success("In Stock");
        await client.RunAsync(watchId, Ct);
        runner.Response = Crash("Error: No price on the page\n    at run (watch.ts:12:5)");
        await client.RunAsync(watchId, Ct);

        await pushover.WaitForRequestsAsync(1, Ct);
        var fields = pushover.Requests.Single().Fields;
        Assert.AreEqual("Error: No price on the page", fields["message"]);
        Assert.AreEqual("-1", fields["priority"]);
    }

    [TestMethod]
    public async Task TheRunsScreenshotIsAttached()
    {
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3];
        var runner = new FakeRunner(new { crash = new { error = "Error: No price" }, screenshot = png });
        var pushover = new FakePushover();
        await using var factory = CreateFactory(runner, pushover);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await SubscribedWatchAsync(client, WatchEvent.Crash);

        await client.RunAsync(watchId, Ct);

        await pushover.WaitForRequestsAsync(1, Ct);
        var attachment = pushover.Requests.Single().Files["attachment"];
        Assert.AreSequenceEqual(png, attachment.Bytes);
        Assert.AreEqual("image/png", attachment.Type?.MediaType);
    }

    [TestMethod]
    [DataRow("https://lookout.example")]
    [DataRow("https://example.com/lookout/")]
    public async Task ANotificationLinksToItsRunWhenLookoutKnowsItsPublicAddress(string publicUrl)
    {
        var runner = new FakeRunner(Crash("Error: No price"));
        var pushover = new FakePushover();
        await using var factory = CreateFactory(
            runner, pushover, new Dictionary<string, string?> { [EnvVars.LookoutPublicUrl] = publicUrl });
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await SubscribedWatchAsync(client, WatchEvent.Crash);

        var run = await client.RunAsync(watchId, Ct);

        await pushover.WaitForRequestsAsync(1, Ct);
        Assert.AreEqual(
            $"{publicUrl.TrimEnd('/')}/watches/{watchId}/runs/{run.Id}", pushover.Requests.Single().Fields["url"]);
    }

    [TestMethod]
    public async Task ANotificationHasNoLinkWhenLookoutDoesntKnowItsPublicAddress()
    {
        var runner = new FakeRunner(Crash("Error: No price"));
        var pushover = new FakePushover();
        await using var factory = CreateFactory(runner, pushover);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await SubscribedWatchAsync(client, WatchEvent.Crash);

        await client.RunAsync(watchId, Ct);

        await pushover.WaitForRequestsAsync(1, Ct);
        Assert.DoesNotContain("url", pushover.Requests.Single().Fields.Keys);
    }

    [TestMethod]
    public async Task AKnownFailureStreakAndItsRecoveryAreSentAtTheWatchsThreshold()
    {
        var runner = new FakeRunner(Success("In Stock"));
        var pushover = new FakePushover();
        await using var factory = CreateFactory(runner, pushover);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var channel = await client.CreateChannelAsync("Phone", Ct);
        var watchId = await client.CreateWatchAsync("Shoe stock", Ct, ("KnownFailureThreshold", "2"));
        await client.SubscribeAsync(watchId, Ct, new
        {
            NotificationChannelId = channel.Id,
            Events = new[] { WatchEvent.KnownFailureStreak, WatchEvent.Recovery },
            Pushover = new { Priority = 0 },
        });

        await client.RunAsync(watchId, Ct);
        runner.Response = KnownFailure("Blocked in this region");
        await client.RunAsync(watchId, Ct);
        await client.RunAsync(watchId, Ct);
        await pushover.WaitForRequestsAsync(1, Ct);
        runner.Response = Success("In Stock");
        await client.RunAsync(watchId, Ct);

        await pushover.WaitForRequestsAsync(2, Ct);
        Assert.AreSequenceEqual(
            ["Known failure 2 times in a row: Blocked in this region", "Recovered: In Stock"],
            pushover.Requests.Select(r => r.Fields["message"]).ToArray());
    }

    [TestMethod]
    public async Task ARunKeepsTheEventItShowedWhetherOrNotAnythingIsSubscribed()
    {
        var runner = new FakeRunner(Success("In Stock"));
        var pushover = new FakePushover();
        await using var factory = CreateFactory(runner, pushover);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await client.CreateWatchAsync("Shoe stock", Ct, ("KnownFailureThreshold", "1"));

        var success = await client.RunAsync(watchId, Ct);
        runner.Response = KnownFailure("Blocked in this region");
        var knownFailure = await client.RunAsync(watchId, Ct);

        Assert.IsNull(success.Event);
        Assert.AreEqual(WatchEvent.KnownFailureStreak, knownFailure.Event);
    }

    [TestMethod]
    public async Task AStreakNotifiedAfterItsThresholdIsLoweredSaysHowLongItIs()
    {
        var runner = new FakeRunner(KnownFailure("Blocked in this region"));
        var pushover = new FakePushover();
        await using var factory = CreateFactory(runner, pushover);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await SubscribedWatchAsync(client, WatchEvent.KnownFailureStreak, ("KnownFailureThreshold", "5"));
        await client.RunAsync(watchId, Ct);
        await client.RunAsync(watchId, Ct);
        await client.RunAsync(watchId, Ct);

        using var lower = await client.PutAsJsonAsync(
            $"/api/Watches/{watchId}", new { Name = "Shoe stock", KnownFailureThreshold = 2 }, Ct);
        Assert.AreEqual(HttpStatusCode.OK, lower.StatusCode, await lower.Content.ReadAsStringAsync(Ct));
        await client.RunAsync(watchId, Ct);

        await pushover.WaitForRequestsAsync(1, Ct);
        Assert.AreEqual("Known failure 4 times in a row: Blocked in this region", pushover.Requests.Single().Fields["message"]);
    }

    [TestMethod]
    public async Task TheKnownFailureThresholdIsKeptWithTheWatch()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var watchId = await client.CreateWatchAsync("Shoe stock", Ct, ("KnownFailureThreshold", "5"));
        Assert.AreEqual(5, (await client.GetFromJsonAsync<WatchSummary>($"/api/Watches/{watchId}", Ct))!.KnownFailureThreshold);

        using var update = await client.PutAsJsonAsync(
            $"/api/Watches/{watchId}", new { Name = "Shoe stock", KnownFailureThreshold = 1 }, Ct);
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(1, (await client.GetFromJsonAsync<WatchSummary>($"/api/Watches/{watchId}", Ct))!.KnownFailureThreshold);

        using var zero = await client.PutAsJsonAsync(
            $"/api/Watches/{watchId}", new { Name = "Shoe stock", KnownFailureThreshold = 0 }, Ct);
        Assert.AreEqual(HttpStatusCode.BadRequest, zero.StatusCode);
    }

    [TestMethod]
    public async Task ARunCutOffByARestartIsMarkedInterrupted()
    {
        // Disposed last, after the next Backend, so it deletes the data they share only once both are done with it
        await using var before = new LookoutFactory(configureServices: services =>
            services.AddHttpClient<RunnerHttpClient>().ConfigurePrimaryHttpMessageHandler(() => new HangingRunner()));
        int watchId;
        int runId;
        {
            using var client = await before.CreateSignedInClientAsync(Ct);
            watchId = await client.CreateWatchAsync("Shoe stock", Ct);
            using var response = await client.PostAsync($"/api/Watches/{watchId}/runs", null, Ct);
            runId = (await response.Content.ReadFromJsonAsync<WatchRun>(Ct))!.Id;
            await WaitForStatusAsync(client, watchId, runId, WatchRunStatus.Running);

            // Stopped without deleting its data, so the next Backend finds the run still running
            await ((IAsyncDisposable)before.Services).DisposeAsync();
        }

        await using var after = new LookoutFactory(dataDirectory: before.DataDirectory);
        using var afterClient = await after.CreateSignedInClientAsync(Ct);
        var run = await WaitForStatusAsync(afterClient, watchId, runId, WatchRunStatus.Done);

        Assert.IsTrue(run.Interrupted);
        Assert.AreEqual("Interrupted because the Backend restarted.", run.Error);
    }

    private async Task<WatchRun> WaitForStatusAsync(HttpClient client, int watchId, int runId, WatchRunStatus status)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var run = await client.GetFromJsonAsync<WatchRun>($"/api/Watches/{watchId}/runs/{runId}", timeout.Token);
            if (run!.Status == status)
            {
                return run;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    /// <summary>
    /// A Runner whose runs never finish.
    /// </summary>
    private sealed class HangingRunner : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    /// A watch that sends <paramref name="watchEvent"/> to a new channel at normal priority.
    /// </summary>
    private async Task<int> SubscribedWatchAsync(
        HttpClient client, WatchEvent watchEvent, params (string Name, string Value)[] fields)
    {
        var channel = await client.CreateChannelAsync("Phone", Ct);
        var watchId = await client.CreateWatchAsync("Shoe stock", Ct, fields);
        await client.SubscribeAsync(watchId, Ct,
            new { NotificationChannelId = channel.Id, Events = new[] { watchEvent }, Pushover = new { Priority = 0 } });
        return watchId;
    }

    private static object Success(string content) => new { success = true, content };

    private static object KnownFailure(string failureReason) => new { success = false, failureReason };

    private static object Crash(string error) => new { crash = new { error } };

    private static LookoutFactory CreateFactory(
        FakeRunner runner, FakePushover pushover, IReadOnlyDictionary<string, string?>? settings = null) => new(
        settings: settings,
        configureServices: services =>
        {
            services.AddHttpClient<RunnerHttpClient>().ConfigurePrimaryHttpMessageHandler(() => runner);
            services.AddHttpClient<PushoverHttpClient>().ConfigurePrimaryHttpMessageHandler(() => pushover);
        });
}
