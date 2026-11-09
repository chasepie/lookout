using System.Net;
using System.Net.Http.Json;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Core;
using ChasePie.Lookout.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Setting up notification channels through the API.
/// </summary>
[TestClass]
public sealed class NotificationChannelsTests
{
    private const string Token = LookoutApi.ApplicationToken;
    private const string UserKey = LookoutApi.UserKey;

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task AChannelIsListedWithoutItsCredentials()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);

        await client.CreateChannelAsync("Phone", Ct, retrySeconds: 45, expireSeconds: 1800);

        var json = await client.GetStringAsync("/api/NotificationChannels", Ct);
        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain(UserKey, json);
        var channel = (await client.GetFromJsonAsync<List<NotificationChannelSummary>>("/api/NotificationChannels", Ct))!
            .Single();
        Assert.AreEqual("Phone", channel.Name);
        Assert.AreEqual(NotificationService.Pushover, channel.Service);
        Assert.AreEqual(45, channel.Pushover!.RetrySeconds);
        Assert.AreEqual(1800, channel.Pushover.ExpireSeconds);
    }

    [TestMethod]
    public async Task ATestNotificationGoesThroughTheChannel()
    {
        var pushover = new FakePushover();
        await using var factory = CreateFactory(pushover);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var channel = await client.CreateChannelAsync("Phone", Ct);

        using var response = await client.PostAsync($"/api/NotificationChannels/{channel.Id}/test", null, Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var fields = pushover.Requests.Single().Fields;
        Assert.AreEqual(Token, fields["token"]);
        Assert.AreEqual(UserKey, fields["user"]);
        Assert.AreEqual("Lookout", fields["title"]);
        Assert.AreEqual("This is a test notification from Lookout.", fields["message"]);
    }

    [TestMethod]
    public async Task ATestNotificationThatFailsSaysWhy()
    {
        var pushover = new FakePushover(HttpStatusCode.BadRequest, """{"status":0,"errors":["application token is invalid"]}""");
        await using var factory = CreateFactory(pushover);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var channel = await client.CreateChannelAsync("Phone", Ct);

        using var response = await client.PostAsync($"/api/NotificationChannels/{channel.Id}/test", null, Ct);

        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains(
            "Pushover refused the notification (400): application token is invalid",
            await response.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task EditingAChannelWithoutItsCredentialsKeepsThem()
    {
        var pushover = new FakePushover();
        await using var factory = CreateFactory(pushover);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var channel = await client.CreateChannelAsync("Phone", Ct);

        using var update = await client.PutAsJsonAsync(
            $"/api/NotificationChannels/{channel.Id}",
            new
            {
                Name = "Phone (quiet)",
                Service = NotificationService.Pushover,
                Pushover = new { RetrySeconds = 30, ExpireSeconds = 600 },
            },
            Ct);
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync(Ct));

        var edited = await client.GetFromJsonAsync<NotificationChannelSummary>($"/api/NotificationChannels/{channel.Id}", Ct);
        Assert.AreEqual("Phone (quiet)", edited!.Name);
        Assert.AreEqual(new PushoverChannelSummary(30, 600), edited.Pushover);
        (await client.PostAsync($"/api/NotificationChannels/{channel.Id}/test", null, Ct)).Dispose();
        Assert.AreEqual(Token, pushover.Requests.Single().Fields["token"]);
        Assert.AreEqual(UserKey, pushover.Requests.Single().Fields["user"]);
    }

    [TestMethod]
    public async Task EditingAChannelsCredentialsReplacesThem()
    {
        var pushover = new FakePushover();
        await using var factory = CreateFactory(pushover);
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var channel = await client.CreateChannelAsync("Phone", Ct);

        using var update = await client.PutAsJsonAsync(
            $"/api/NotificationChannels/{channel.Id}",
            new
            {
                Name = "Phone",
                Service = NotificationService.Pushover,
                Pushover = new { ApplicationToken = "new-token", UserKey = "new-user", RetrySeconds = 60, ExpireSeconds = 3600 },
            },
            Ct);
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync(Ct));

        (await client.PostAsync($"/api/NotificationChannels/{channel.Id}/test", null, Ct)).Dispose();
        Assert.AreEqual("new-token", pushover.Requests.Single().Fields["token"]);
        Assert.AreEqual("new-user", pushover.Requests.Single().Fields["user"]);
    }

    internal static LookoutFactory CreateFactory(FakePushover pushover) => new(configureServices: services =>
        services.AddHttpClient<PushoverHttpClient>().ConfigurePrimaryHttpMessageHandler(() => pushover));
}
