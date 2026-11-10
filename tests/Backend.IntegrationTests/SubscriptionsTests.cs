using System.Net;
using System.Net.Http.Json;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Choosing, through the API, which of a watch's events go to which notification channels, and how.
/// </summary>
[TestClass]
public sealed class SubscriptionsTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task AWatchsSubscriptionsAreSavedWithTheirEventsAndOptions()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var channelId = (await client.CreateChannelAsync("Phone", Ct)).Id;
        var watchId = await client.CreateWatchAsync("Shoe stock", Ct);

        using var response = await client.PutSubscriptionsAsync(watchId, Ct,
            new
            {
                NotificationChannelId = channelId,
                Events = new[] { WatchEvent.ContentChange, WatchEvent.Recovery },
                Pushover = new { Priority = 1, Sound = "cashregister" },
            },
            new
            {
                NotificationChannelId = channelId,
                Events = new[] { WatchEvent.Crash },
                Pushover = new { Priority = -1, Sound = (string?)null },
            });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var subscriptions = (await client.GetFromJsonAsync<List<SubscriptionDto>>($"/api/Watches/{watchId}/subscriptions", Ct))!;
        Assert.HasCount(2, subscriptions);
        Assert.AreEqual(channelId, subscriptions[0].NotificationChannelId);
        Assert.AreSequenceEqual(new[] { WatchEvent.ContentChange, WatchEvent.Recovery }, subscriptions[0].Events);
        Assert.AreEqual(1, subscriptions[0].Pushover!.Priority);
        Assert.AreEqual("cashregister", subscriptions[0].Pushover!.Sound);
        Assert.AreSequenceEqual(new[] { WatchEvent.Crash }, subscriptions[1].Events);
        Assert.AreEqual(-1, subscriptions[1].Pushover!.Priority);
        Assert.IsNull(subscriptions[1].Pushover!.Sound);
    }

    [TestMethod]
    public async Task AnEventCantGoToTheSameChannelTwice()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var channelId = (await client.CreateChannelAsync("Phone", Ct)).Id;
        var watchId = await client.CreateWatchAsync("Shoe stock", Ct);

        using var response = await client.PutSubscriptionsAsync(watchId, Ct,
            new { NotificationChannelId = channelId, Events = new[] { WatchEvent.Crash }, Pushover = new { Priority = 1 } },
            new { NotificationChannelId = channelId, Events = new[] { WatchEvent.Crash }, Pushover = new { Priority = -1 } });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Crash is sent to Phone more than once.", await response.Content.ReadAsStringAsync(Ct));
        var subscriptions = await client.GetFromJsonAsync<List<SubscriptionDto>>($"/api/Watches/{watchId}/subscriptions", Ct);
        Assert.IsEmpty(subscriptions!);
    }

    [TestMethod]
    [DataRow(-3)]
    [DataRow(3)]
    public async Task APriorityMustBeOnePushoverHas(int priority)
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var channelId = (await client.CreateChannelAsync("Phone", Ct)).Id;
        var watchId = await client.CreateWatchAsync("Shoe stock", Ct);

        using var response = await client.PutSubscriptionsAsync(watchId, Ct,
            new { NotificationChannelId = channelId, Events = new[] { WatchEvent.Crash }, Pushover = new { Priority = priority } });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task AChannelListsTheWatchesThatUseIt()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var channelId = (await client.CreateChannelAsync("Phone", Ct)).Id;
        var shoes = await client.CreateWatchAsync("Shoe stock", Ct);
        var price = await client.CreateWatchAsync("Price", Ct);
        await client.CreateWatchAsync("Unsubscribed", Ct);
        (await client.PutSubscriptionsAsync(shoes, Ct,
            new { NotificationChannelId = channelId, Events = new[] { WatchEvent.Crash }, Pushover = new { Priority = 0 } },
            new { NotificationChannelId = channelId, Events = new[] { WatchEvent.Recovery }, Pushover = new { Priority = 0 } }))
            .Dispose();
        (await client.PutSubscriptionsAsync(price, Ct,
            new { NotificationChannelId = channelId, Events = new[] { WatchEvent.Crash }, Pushover = new { Priority = 0 } }))
            .Dispose();

        var channel = await client.GetFromJsonAsync<NotificationChannelSummary>($"/api/NotificationChannels/{channelId}", Ct);

        Assert.AreSequenceEqual(["Price", "Shoe stock"], channel!.Watches.Select(w => w.Name).ToArray());
    }

    [TestMethod]
    public async Task DeletingAChannelDeletesItsSubscriptions()
    {
        await using var factory = new LookoutFactory();
        using var client = await factory.CreateSignedInClientAsync(Ct);
        var phone = (await client.CreateChannelAsync("Phone", Ct)).Id;
        var tablet = (await client.CreateChannelAsync("Tablet", Ct)).Id;
        var watchId = await client.CreateWatchAsync("Shoe stock", Ct);
        (await client.PutSubscriptionsAsync(watchId, Ct,
            new { NotificationChannelId = phone, Events = new[] { WatchEvent.Crash }, Pushover = new { Priority = 0 } },
            new { NotificationChannelId = tablet, Events = new[] { WatchEvent.Crash }, Pushover = new { Priority = 0 } }))
            .Dispose();

        using var response = await client.DeleteAsync($"/api/NotificationChannels/{phone}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        var channels = await client.GetFromJsonAsync<List<NotificationChannelSummary>>("/api/NotificationChannels", Ct);
        Assert.AreSequenceEqual(new[] { tablet }, channels!.Select(c => c.Id).ToArray());
        var subscriptions = await client.GetFromJsonAsync<List<SubscriptionDto>>($"/api/Watches/{watchId}/subscriptions", Ct);
        Assert.AreSequenceEqual(new[] { tablet }, subscriptions!.Select(s => s.NotificationChannelId).ToArray());
    }
}
