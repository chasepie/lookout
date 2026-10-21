using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;

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

        var sent = factory.Channels.Sent.Single();
        Assert.AreEqual("Phone", sent.ChannelName);
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
        var afterFirst = factory.Channels.Sent.ToList();
        await RunNow(client, watch, times: 3);

        var sent = factory.Channels.Sent.Single();
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
        var afterTwo = factory.Channels.Sent.ToList();
        await RunNow(client, watch);
        var afterThree = factory.Channels.Sent.ToList();
        await RunNow(client, watch, times: 2);

        Assert.IsEmpty(afterTwo);
        var sent = afterThree.Single();
        Assert.AreSame(sent, factory.Channels.Sent.Single());
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

        Assert.IsEmpty(factory.Channels.Sent);
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

        var sent = factory.Channels.Sent.Select(item => item.Notification).ToList();
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
            factory.Channels.Sent.Select(item => item.Notification.Kind));
        Assert.AreEqual("£20 → £19", factory.Channels.Sent.Last().Notification.Message);
    }

    [TestMethod]
    public async Task SettingTheBaselineSendsNone()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), Succeeded("£20"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Phone");

        await RunNow(client, watch, times: 2);

        Assert.IsEmpty(factory.Channels.Sent);
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
            factory.Channels.Sent.Select(item => item.Notification.Message));
    }

    [TestMethod]
    public async Task OnlyTheWatchsChosenChannelsReceiveItsNotifications()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), Succeeded("£19"), Succeeded("£20"), Succeeded("£18"));
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var phone = await CreateChannel(client, "Phone");
        var server = await CreateChannel(client, "Deals server");
        await CreateChannel(client, "Unused");
        var gadget = await CreateWatch(client);
        var widget = await CreateWatch(client);
        await SetChannels(client, widget, phone, server);
        await SetChannels(client, gadget, server);

        await RunNow(client, widget, times: 2);
        await SetChannels(client, widget, phone);
        await RunNow(client, widget, times: 2);

        Assert.AreSequenceEqual(["Deals server", "Phone", "Phone", "Phone"],
            factory.Channels.Sent.Select(item => item.ChannelName).Order(StringComparer.Ordinal));
        var watch = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{widget}", testContext.CancellationToken);
        Assert.AreSequenceEqual([phone], watch!["channelIds"]!.AsArray().Select(id => id!.GetValue<string>()));
    }

    [TestMethod]
    public async Task AChannelThatFailsToSendNeitherFailsTheRunNorStopsOtherChannels()
    {
        await using var factory = new LookoutWebApplicationFactory();
        Arrange(factory, Succeeded("£20"), Succeeded("£19"));
        factory.Channels.OnSend = (channel, _) => channel.Name == "Broken"
            ? throw new HttpRequestException("Discord returned HTTP 404 (Not Found).")
            : Task.CompletedTask;
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var watch = await CreateWatchNotifying(client, "Broken", "Phone");

        await RunNow(client, watch, times: 2);

        Assert.AreEqual("Phone", factory.Channels.Sent.Single().ChannelName);
        var runs = await client.GetFromJsonAsync<JsonArray>($"/api/watches/{watch}/runs", testContext.CancellationToken);
        Assert.IsTrue(runs![0]!["isChange"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task ADeletedChannelIsNoLongerChosenByAnyWatch()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var phone = await CreateChannel(client, "Phone");
        var server = await CreateChannel(client, "Deals server");
        var watch = await CreateWatch(client);
        await SetChannels(client, watch, phone, server);

        var response = await client.DeleteAsync(new Uri($"/api/channels/{phone}", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        var updated = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{watch}", testContext.CancellationToken);
        Assert.AreSequenceEqual([server], updated!["channelIds"]!.AsArray().Select(id => id!.GetValue<string>()));
        var channels = await client.GetFromJsonAsync<JsonArray>("/api/channels", testContext.CancellationToken);
        Assert.AreSequenceEqual(["Deals server"], channels!.Select(channel => channel!["name"]!.GetValue<string>()));
    }

    [TestMethod]
    public async Task ChoosingAChannelThatDoesNotExistIsRejectedWithoutChangingTheWatch()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var phone = await CreateChannel(client, "Phone");
        var watch = await CreateWatch(client);
        await SetChannels(client, watch, phone);

        var response = await client.PutAsJsonAsync($"/api/watches/{watch}/channels",
            new { channelIds = new[] { Guid.NewGuid() } }, testContext.CancellationToken);
        var missingWatch = await client.PutAsJsonAsync($"/api/watches/{Guid.NewGuid()}/channels",
            new { channelIds = Array.Empty<Guid>() }, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, missingWatch.StatusCode);
        var unchanged = await client.GetFromJsonAsync<JsonObject>($"/api/watches/{watch}", testContext.CancellationToken);
        Assert.AreSequenceEqual([phone], unchanged!["channelIds"]!.AsArray().Select(id => id!.GetValue<string>()));
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
        while (factory.Channels.Sent.IsEmpty)
        {
            await Task.Delay(20, timeout.Token);
        }
        Assert.AreEqual("£20 → £19", factory.Channels.Sent.Single().Notification.Message);
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

    /// <summary>Creates a Watch that notifies new Channels of the given names, and returns its ID.</summary>
    private async Task<string> CreateWatchNotifying(HttpClient client, params string[] channelNames)
    {
        var channelIds = new List<string>();
        foreach (var name in channelNames)
        {
            channelIds.Add(await CreateChannel(client, name));
        }
        var watch = await CreateWatch(client);
        await SetChannels(client, watch, [.. channelIds]);
        return watch;
    }

    private async Task<string> CreateChannel(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/channels",
            new { name, kind = "discord", webhookUrl = "https://discord.com/api/webhooks/123/token" }, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["id"]!.GetValue<string>();
    }

    private async Task<string> CreateWatch(HttpClient client)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Widget price"), "name" },
            { new StringContent("https://example.com/widget"), "notificationUrl" },
            { new StringContent("export default {};"), "script", "price.ts" },
            { new StringContent("interval"), "scheduleKind" },
            { new StringContent("1"), "every" },
            { new StringContent("hours"), "unit" }
        };
        var response = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["id"]!.GetValue<string>();
    }

    private async Task SetChannels(HttpClient client, string watch, params string[] channelIds)
    {
        var response = await client.PutAsJsonAsync($"/api/watches/{watch}/channels", new { channelIds }, testContext.CancellationToken);
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
