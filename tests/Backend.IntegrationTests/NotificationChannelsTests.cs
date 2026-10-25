using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class NotificationChannelsTests(TestContext testContext)
{
    private const string WebhookUrl = "https://discord.com/api/webhooks/123/secret-webhook-token";
    private const string UserKey = "uQiRzpo4DXghDmr9QzzfQu27cmVRsG";
    private const string AppToken = "azGDORePK8gMaC0QOYAMyEEuzJnyUi";

    [TestMethod]
    public async Task ConfiguresDiscordAndPushoverNotificationChannelsWithoutEverReturningTheirSecrets()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var phone = await client.PostAsJsonAsync("/api/notification-channels",
            new { name = " Phone ", kind = "pushover", userKey = UserKey, appToken = AppToken }, testContext.CancellationToken);
        var server = await client.PostAsJsonAsync("/api/notification-channels",
            new { name = "Deals server", kind = "discord", webhookUrl = WebhookUrl }, testContext.CancellationToken);
        using var list = await client.GetAsync(new Uri("/api/notification-channels", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Created, phone.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, server.StatusCode);
        var body = await list.Content.ReadAsStringAsync(testContext.CancellationToken);
        var notificationChannels = JsonNode.Parse(body)!.AsArray();
        Assert.HasCount(2, notificationChannels);
        Assert.AreEqual("Deals server", notificationChannels[0]!["name"]!.GetValue<string>());
        Assert.AreEqual("discord", notificationChannels[0]!["kind"]!.GetValue<string>());
        Assert.AreEqual("Phone", notificationChannels[1]!["name"]!.GetValue<string>());
        Assert.AreEqual("pushover", notificationChannels[1]!["kind"]!.GetValue<string>());
        foreach (var response in new[] { body, await phone.Content.ReadAsStringAsync(testContext.CancellationToken),
                     await server.Content.ReadAsStringAsync(testContext.CancellationToken) })
        {
            Assert.DoesNotContain("secret-webhook-token", response);
            Assert.DoesNotContain(UserKey, response);
            Assert.DoesNotContain(AppToken, response);
        }
    }

    [TestMethod]
    [DataRow("""{ "name": " ", "kind": "discord", "webhookUrl": "https://discord.com/api/webhooks/1/a" }""", "Name must contain")]
    [DataRow("""{ "name": "Server", "kind": "discord", "webhookUrl": "discord.com/api/webhooks/1/a" }""", "Webhook URL must be")]
    [DataRow("""{ "name": "Server", "kind": "discord", "webhookUrl": "ftp://discord.com/a" }""", "Webhook URL must be")]
    [DataRow("""{ "name": "Phone", "kind": "pushover", "userKey": "short", "appToken": "azGDORePK8gMaC0QOYAMyEEuzJnyUi" }""", "Pushover's user key")]
    [DataRow("""{ "name": "Phone", "kind": "pushover", "userKey": "uQiRzpo4DXghDmr9QzzfQu27cmVRsG" }""", "Pushover's user key")]
    [DataRow("""{ "name": "Phone" }""", "Choose a Discord or Pushover Notification channel.")]
    public async Task RejectsAnIncompleteNotificationChannel(string json, string message)
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(new Uri("/api/notification-channels", UriKind.Relative), content, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.StartsWith(message, problem!["detail"]!.GetValue<string>());
        Assert.IsEmpty((await client.GetFromJsonAsync<JsonArray>("/api/notification-channels", testContext.CancellationToken))!);
    }

    [TestMethod]
    public async Task SendsATestNotificationToTheChosenNotificationChannelOnly()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var phone = await CreateNotificationChannel(client, "Phone");
        await CreateNotificationChannel(client, "Deals server");

        var response = await client.PostAsync(new Uri($"/api/notification-channels/{phone}/test", UriKind.Relative), null,
            testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        var sent = factory.NotificationChannels.Sent.Single();
        Assert.AreEqual("Phone", sent.NotificationChannelName);
        Assert.AreEqual(NotificationKind.Test, sent.Notification.Kind);
    }

    [TestMethod]
    public async Task DeliversTestNotificationsThroughDiscordAndPushover()
    {
        await using var endpoint = await FakeHttpServer.StartAsync(testContext.CancellationToken);
        endpoint.Respond = context => context.Response.WriteAsJsonAsync(new { status = 1 }, context.RequestAborted);
        await using var factory = new LookoutWebApplicationFactory { UseRealNotificationChannels = true };
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Notifications:PushoverBaseUrl", new Uri(endpoint.BaseAddress, "pushover").ToString()));
        using var client = await host.CreateSignedInClientAsync(testContext.CancellationToken);
        var discord = await client.PostAsJsonAsync("/api/notification-channels",
            new { name = "Deals server", kind = "discord", webhookUrl = new Uri(endpoint.BaseAddress, "api/webhooks/1/a") },
            testContext.CancellationToken);
        var pushover = await client.PostAsJsonAsync("/api/notification-channels",
            new { name = "Phone", kind = "pushover", userKey = UserKey, appToken = AppToken }, testContext.CancellationToken);

        foreach (var created in new[] { discord, pushover })
        {
            var id = (await created.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken))!["id"]!.GetValue<string>();
            var response = await client.PostAsync(new Uri($"/api/notification-channels/{id}/test", UriKind.Relative), null,
                testContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.AreSequenceEqual(["/api/webhooks/1/a", "/pushover/1/messages.json"],
            endpoint.Requests.Select(request => request.PathAndQuery));
    }

    [TestMethod]
    public async Task ExplainsWhyATestNotificationCouldNotBeSent()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.NotificationChannels.OnSend = (_, _) => throw new HttpRequestException("Discord returned HTTP 404 (Not Found): Unknown Webhook");
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = await CreateNotificationChannel(client, "Deals server");

        var response = await client.PostAsync(new Uri($"/api/notification-channels/{id}/test", UriKind.Relative), null,
            testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.AreEqual("Could not send the test notification: Discord returned HTTP 404 (Not Found): Unknown Webhook",
            problem!["detail"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ABugInSendingATestNotificationIsNotPassedOffAsTheServiceRefusingIt()
    {
        await using var factory = new LookoutWebApplicationFactory();
        factory.NotificationChannels.OnSend = (_, _) => throw new InvalidOperationException("Internal detail");
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);
        var id = await CreateNotificationChannel(client, "Deals server");

        // The test host passes unhandled exceptions to the client, where a real host would answer 500 and log them.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.PostAsync(
            new Uri($"/api/notification-channels/{id}/test", UriKind.Relative), null, testContext.CancellationToken));
    }

    [TestMethod]
    public async Task TestingAMissingNotificationChannelIsNotFound()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var response = await client.PostAsync(new Uri($"/api/notification-channels/{Guid.NewGuid()}/test", UriKind.Relative), null,
            testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<string> CreateNotificationChannel(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/notification-channels", new { name, kind = "discord", webhookUrl = WebhookUrl },
            testContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        return created!["id"]!.GetValue<string>();
    }
}
