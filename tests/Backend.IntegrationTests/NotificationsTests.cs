using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class NotificationsTests(TestContext testContext)
{
    [TestMethod]
    public async Task AChangeSendsOneNotificationWithTheOldAndNewContentAndTheNotificationUrl()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), Succeeded(" £19 \n"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Phone");

        await RunNow(client, watch, times: 2);

        var sent = factory.NotificationChannels.Sent.Single();
        Assert.AreEqual("Phone", sent.NotificationChannelName);
        Assert.AreEqual(NotificationKind.Change, sent.Notification.Kind);
        Assert.AreEqual("Widget price changed", sent.Notification.Title);
        Assert.AreEqual("£20 → £19", sent.Notification.Message);
        Assert.AreEqual("https://example.com/widget", sent.Notification.Url);
    }

    [TestMethod]
    public async Task AStreakOfErroredRunsSendsOneNotificationOnItsFirstRun()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), Errored("Selector .price not found"), Errored("Timed out"), KnownFailure("Access denied"),
            Errored("Timed out"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Phone");

        await RunNow(client, watch, times: 2);
        var afterFirst = factory.NotificationChannels.Sent.ToList();
        await RunNow(client, watch, times: 3);

        var sent = factory.NotificationChannels.Sent.Single();
        Assert.AreSame(sent, afterFirst.Single());
        Assert.AreEqual(NotificationKind.Errored, sent.Notification.Kind);
        Assert.AreEqual("Widget price errored", sent.Notification.Title);
        Assert.AreEqual("Selector .price not found", sent.Notification.Message);
        Assert.AreEqual("https://example.com/widget", sent.Notification.Url);
    }

    [TestMethod]
    public async Task ThreeKnownFailuresInARowSendOneNotification()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), KnownFailure("Blocked"), KnownFailure("Captcha"), KnownFailure(" Access denied "),
            KnownFailure("Access denied"), KnownFailure("Access denied"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Phone");

        await RunNow(client, watch, times: 3);
        var afterTwo = factory.NotificationChannels.Sent.ToList();
        await RunNow(client, watch);
        var afterThree = factory.NotificationChannels.Sent.ToList();
        await RunNow(client, watch, times: 2);

        Assert.IsEmpty(afterTwo);
        var sent = afterThree.Single();
        Assert.AreSame(sent, factory.NotificationChannels.Sent.Single());
        Assert.AreEqual(NotificationKind.KnownFailures, sent.Notification.Kind);
        Assert.AreEqual("Widget price failed 3 times in a row", sent.Notification.Title);
        Assert.AreEqual("Access denied", sent.Notification.Message);
        Assert.AreEqual("https://example.com/widget", sent.Notification.Url);
    }

    [TestMethod]
    public async Task KnownFailuresBrokenUpByASucceededRunSendNone()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), KnownFailure("Blocked"), KnownFailure("Blocked"), Succeeded("£20"),
            KnownFailure("Blocked"), KnownFailure("Blocked"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Phone");

        await RunNow(client, watch, times: 6);

        Assert.IsEmpty(factory.NotificationChannels.Sent);
    }

    [TestMethod]
    [DataRow(RunOutcome.Errored, 1)]
    [DataRow(RunOutcome.KnownFailure, 3)]
    public async Task RecoveringFromANotifiedFailureStreakSendsOneNotification(RunOutcome outcome, int failures)
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(Succeeded("£20"));
        for (var failure = 0; failure < failures + 1; failure++)
        {
            factory.Runner.Results.Enqueue(new RunnerResult { Outcome = outcome, DurationMs = 1 });
        }
        Arrange(factory, Succeeded(" £20 "), Succeeded("£20"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Phone");

        await RunNow(client, watch, times: failures + 4);

        var sent = factory.NotificationChannels.Sent.Select(item => item.Notification).ToList();
        Assert.HasCount(2, sent);
        Assert.AreEqual(NotificationKind.Recovered, sent[1].Kind);
        Assert.AreEqual("Widget price recovered", sent[1].Title);
        Assert.AreEqual("It succeeded again: £20", sent[1].Message);
        Assert.AreEqual("https://example.com/widget", sent[1].Url);
    }

    [TestMethod]
    public async Task RecoveringWithDifferentContentSendsTheRecoveryThenTheChange()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), Errored("Timed out"), Succeeded("£19"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Phone");

        await RunNow(client, watch, times: 3);

        Assert.AreSequenceEqual([NotificationKind.Errored, NotificationKind.Recovered, NotificationKind.Change],
            factory.NotificationChannels.Sent.Select(item => item.Notification.Kind));
        Assert.AreEqual("£20 → £19", factory.NotificationChannels.Sent.Last().Notification.Message);
    }

    [TestMethod]
    public async Task SettingTheBaselineSendsNone()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), Succeeded("£20"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Phone");

        await RunNow(client, watch, times: 2);

        Assert.IsEmpty(factory.NotificationChannels.Sent);
    }

    [TestMethod]
    public async Task SettingTheBaselineAfterANotifiedFailureStreakSendsNoRecovery()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Errored("Selector .price not found"), Succeeded("£20"), Errored("Timed out"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Phone");

        await RunNow(client, watch, times: 3);

        // The first Run sets no Baseline, and the second sets it silently; the third starts a new streak.
        Assert.AreSequenceEqual(["Selector .price not found", "Timed out"],
            factory.NotificationChannels.Sent.Select(item => item.Notification.Message));
    }

    [TestMethod]
    public async Task OnlyTheWatchsChosenNotificationChannelsReceiveItsNotifications()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), Succeeded("£19"), Succeeded("£20"), Succeeded("£18"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var phone = await CreateNotificationChannel(client, "Phone");
        var server = await CreateNotificationChannel(client, "Deals server");
        await CreateNotificationChannel(client, "Unused");
        var gadget = await CreateWatch(client);
        var widget = await CreateWatch(client);
        await SetNotificationChannels(client, widget, phone, server);
        await SetNotificationChannels(client, gadget, server);

        await RunNow(client, widget, times: 2);
        await SetNotificationChannels(client, widget, phone);
        await RunNow(client, widget, times: 2);

        Assert.AreSequenceEqual(["Deals server", "Phone", "Phone", "Phone"],
            factory.NotificationChannels.Sent.Select(item => item.NotificationChannelName).Order(StringComparer.Ordinal));
        var watch = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{widget}", testContext.CancellationToken);
        Assert.AreSequenceEqual([phone], watch!["notificationChannelIds"]!.AsArray().Select(id => id!.GetValue<string>()));
    }

    [TestMethod]
    public async Task AWatchCanChooseItsNotificationChannelsWhenItIsCreated()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), Succeeded("£19"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var phone = await CreateNotificationChannel(client, "Phone");
        await CreateNotificationChannel(client, "Deals server");

        var created = await CreateWatch(client, phone);
        await RunNow(client, created, times: 2);

        Assert.AreEqual("Phone", factory.NotificationChannels.Sent.Single().NotificationChannelName);
        var watch = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{created}", testContext.CancellationToken);
        Assert.AreSequenceEqual([phone], watch!["notificationChannelIds"]!.AsArray().Select(id => id!.GetValue<string>()));
    }

    [TestMethod]
    public async Task CreatingAWatchWithANotificationChannelThatDoesNotExistIsRejected()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        using var form = WatchForm(Guid.NewGuid().ToString());
        var response = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsEmpty((await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken))!);
    }

    [TestMethod]
    public async Task AChangeOfLongContentStillShowsTheStartOfTheNewContent()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded(new string('a', 3000)), Succeeded(new string('b', 3000)));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Phone");

        await RunNow(client, watch, times: 2);

        // Short enough for Pushover's 1024 characters, with both sides shortened rather than the new content cut off.
        var message = factory.NotificationChannels.Sent.Single().Notification.Message;
        Assert.IsLessThanOrEqualTo(1024, message.Length);
        Assert.Contains("a… → bbb", message);
        Assert.EndsWith("b…", message);
    }

    [TestMethod]
    public async Task ShorteningContentNeverSplitsACharacter()
    {
        await using var factory = new LookoutWebApplicationFactory();
        // The emoji, two UTF-16 code units, straddles where each content is cut.
        var content = new string('a', 478) + "🏷️" + new string('a', 100);
        Arrange(factory, Succeeded("£20"), Succeeded(content));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Phone");

        await RunNow(client, watch, times: 2);

        var message = factory.NotificationChannels.Sent.Single().Notification.Message;
        Assert.DoesNotContain(Rune.ReplacementChar, message.EnumerateRunes().ToList());
        Assert.EndsWith(new string('a', 478) + "…", message);
    }

    [TestMethod]
    public async Task ANotificationChannelThatFailsToSendNeitherFailsTheRunNorStopsOtherNotificationChannels()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), Succeeded("£19"));
        factory.NotificationChannels.OnSend = (notificationChannel, _) => notificationChannel.Name == "Broken"
            ? throw new HttpRequestException("Discord returned HTTP 404 (Not Found).")
            : Task.CompletedTask;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Broken", "Phone");

        await RunNow(client, watch, times: 2);

        Assert.AreEqual("Phone", factory.NotificationChannels.Sent.Single().NotificationChannelName);
        var runs = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{watch}/runs", testContext.CancellationToken);
        Assert.IsTrue(runs![0]!["isChange"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ADeletedNotificationChannelIsNoLongerChosenByAnyWatch()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var phone = await CreateNotificationChannel(client, "Phone");
        var server = await CreateNotificationChannel(client, "Deals server");
        var watch = await CreateWatch(client);
        await SetNotificationChannels(client, watch, phone, server);

        var response = await client.DeleteAsync(new Uri($"/api/notification-channels/{phone}", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        var updated = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{watch}", testContext.CancellationToken);
        Assert.AreSequenceEqual([server], updated!["notificationChannelIds"]!.AsArray().Select(id => id!.GetValue<string>()));
        var notificationChannels = await client.GetFromJsonAsync<JsonArray>("/api/notification-channels", testContext.CancellationToken);
        Assert.AreSequenceEqual(["Deals server"], notificationChannels!.Select(notificationChannel => notificationChannel!["name"]!.GetValue<string>()));
    }

    [TestMethod]
    public async Task OverlappingChangesToAWatchsNotificationChannelsNeverCombine()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var interceptor = new HoldFirstWriteInterceptor("WatchNotificationChannels");
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<LookoutDbContext>(options => options.AddInterceptors(interceptor))));
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        var phone = await CreateNotificationChannel(client, "Phone");
        var server = await CreateNotificationChannel(client, "Deals server");
        var watch = await CreateWatch(client);

        var first = client.PutAsJsonAsync($"/api/watches/{watch}/notification-channels", new { notificationChannelIds = new[] { phone } },
            testContext.CancellationToken);
        await interceptor.FirstHeld.Task.WaitAsync(TimeSpan.FromSeconds(10), testContext.CancellationToken);
        var second = client.PutAsJsonAsync($"/api/watches/{watch}/notification-channels", new { notificationChannelIds = new[] { server } },
            testContext.CancellationToken);
        // The second change either reaches its own write or waits behind the first, which is then let go.
        await Task.WhenAny(interceptor.OtherArrived.Task, Task.Delay(500, testContext.CancellationToken));
        interceptor.Release();
        var responses = await Task.WhenAll(first, second);

        var saved = (await client.GetFromJsonAsync<JsonObject>($"/api/watches/{watch}", testContext.CancellationToken))!
            ["notificationChannelIds"]!.AsArray().Select(id => id!.GetValue<string>()).ToList();
        var succeeded = responses.Count(response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.IsGreaterThanOrEqualTo(1, succeeded, "At least one of the changes is saved.");
        Assert.HasCount(1, saved, $"Saved {string.Join(", ", saved)}, a combination of the two changes.");
    }

    [TestMethod]
    public async Task ChangingAWatchsNotificationChannelsWhileItRunsKeepsTheRunAndNotifiesTheNewChoice()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.Runner.Results.Enqueue(Succeeded("£20"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var phone = await CreateNotificationChannel(client, "Phone");
        var server = await CreateNotificationChannel(client, "Deals server");
        var watch = await CreateWatch(client);
        await SetNotificationChannels(client, watch, phone);
        await RunNow(client, watch);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<RunnerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Runner.OnExecute = (_, _) => { started.TrySetResult(); return finished.Task; };
        var run = client.PostAsync(new Uri($"/api/watches/{watch}/runs", UriKind.Relative), null, testContext.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), testContext.CancellationToken);

        await SetNotificationChannels(client, watch, server);
        finished.SetResult(Succeeded("£19"));

        Assert.AreEqual(HttpStatusCode.OK, (await run).StatusCode);
        Assert.AreEqual("Deals server", factory.NotificationChannels.Sent.Single().NotificationChannelName);
    }

    [TestMethod]
    public async Task ChoosingANotificationChannelThatDoesNotExistIsRejectedWithoutChangingTheWatch()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var phone = await CreateNotificationChannel(client, "Phone");
        var watch = await CreateWatch(client);
        await SetNotificationChannels(client, watch, phone);

        var response = await client.PutAsJsonAsync($"/api/watches/{watch}/notification-channels",
            new { notificationChannelIds = new[] { Guid.NewGuid() } }, testContext.CancellationToken);
        var missingWatch = await client.PutAsJsonAsync($"/api/watches/{Guid.NewGuid()}/notification-channels",
            new { notificationChannelIds = Array.Empty<Guid>() }, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, missingWatch.StatusCode);
        var unchanged = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{watch}", testContext.CancellationToken);
        Assert.AreSequenceEqual([phone], unchanged!["notificationChannelIds"]!.AsArray().Select(id => id!.GetValue<string>()));
    }

    [TestMethod]
    public async Task AScheduledRunNotifiesToo()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), Succeeded("£19"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Phone");
        await RunNow(client, watch);

        factory.Time.Advance(TimeSpan.FromHours(1));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (factory.NotificationChannels.Sent.IsEmpty)
        {
            await Task.Delay(20, timeout.Token);
        }
        Assert.AreEqual("£20 → £19", factory.NotificationChannels.Sent.Single().Notification.Message);
    }

    private static void Arrange(LookoutWebApplicationFactory factory, params RunnerResult[] results)
    {
        foreach (var result in results)
        {
            factory.Runner.Results.Enqueue(result);
        }
    }

    private static RunnerResult Succeeded(string content) =>
        new() { Outcome = RunOutcome.Succeeded, Content = content, DurationMs = 1 };

    private static RunnerResult KnownFailure(string details) =>
        new() { Outcome = RunOutcome.KnownFailure, Details = details, DurationMs = 1 };

    private static RunnerResult Errored(string errorMessage) =>
        new() { Outcome = RunOutcome.Errored, ErrorMessage = errorMessage, DurationMs = 1 };

    /// <summary>Creates a Watch that notifies new Notification channels of the given names, and returns its ID.</summary>
    private async Task<string> CreateWatchNotifying(HttpClient client, params string[] notificationChannelNames)
    {
        var notificationChannelIds = new List<string>();
        foreach (var name in notificationChannelNames)
        {
            notificationChannelIds.Add(await CreateNotificationChannel(client, name));
        }
        var watch = await CreateWatch(client);
        await SetNotificationChannels(client, watch, [.. notificationChannelIds]);
        return watch;
    }

    private async Task<string> CreateNotificationChannel(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/notification-channels",
            new { name, kind = "discord", webhookUrl = "https://discord.com/api/webhooks/123/token" }, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["id"]!.GetValue<string>();
    }

    private async Task<string> CreateWatch(HttpClient client, params string[] notificationChannelIds)
    {
        using var form = WatchForm(notificationChannelIds);
        var response = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["id"]!.GetValue<string>();
    }

    private static MultipartFormDataContent WatchForm(params string[] notificationChannelIds)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("Widget price"), "name" },
            { new StringContent("https://example.com/widget"), "notificationUrl" },
            { new StringContent("export default {};"), "script", "price.ts" },
            { new StringContent("interval"), "scheduleKind" },
            { new StringContent("1"), "every" },
            { new StringContent("hours"), "unit" }
        };
        foreach (var id in notificationChannelIds)
        {
            form.Add(new StringContent(id), "notificationChannelIds");
        }
        return form;
    }

    private async Task SetNotificationChannels(HttpClient client, string watch, params string[] notificationChannelIds)
    {
        var response = await client.PutAsJsonAsync($"/api/watches/{watch}/notification-channels", new { notificationChannelIds }, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task RunNow(HttpClient client, string watch, int times = 1)
    {
        for (var run = 0; run < times; run++)
        {
            var response = await client.PostAsync(new Uri($"/api/watches/{watch}/runs", UriKind.Relative), null,
                testContext.CancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }
}
