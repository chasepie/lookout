using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class NotificationChannelSenderTests(TestContext testContext)
{
    private const string UserKey = "uQiRzpo4DXghDmr9QzzfQu27cmVRsG";
    private const string AppToken = "azGDORePK8gMaC0QOYAMyEEuzJnyUi";
    private static readonly string[] PushoverErrors = ["application token is invalid", "user identifier is not valid"];
    private static readonly Notification Change = Notification.Change("Widget price", "£20", "£19", "https://example.com/widget");

    [TestMethod]
    public async Task DiscordPostsAnEmbedToTheWebhookUrlWithoutPingingAnyone()
    {
        await using var server = await FakeHttpServer.StartAsync(testContext.CancellationToken);
        using var http = new HttpClient();
        var notificationChannel = Discord(new Uri(server.BaseAddress, "api/webhooks/123/webhook-token"));

        await new DiscordSender(http).SendAsync(notificationChannel, Change, testContext.CancellationToken);

        var request = server.Requests.Single();
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual("/api/webhooks/123/webhook-token", request.PathAndQuery);
        Assert.StartsWith("application/json", request.ContentType!);
        var body = JsonNode.Parse(request.Body)!;
        Assert.AreEqual("Lookout", body["username"]!.GetValue<string>());
        var embed = body["embeds"]!.AsArray().Single()!;
        Assert.AreEqual("Widget price changed", embed["title"]!.GetValue<string>());
        Assert.AreEqual("£20 → £19", embed["description"]!.GetValue<string>());
        Assert.AreEqual("https://example.com/widget", embed["url"]!.GetValue<string>());
        // Page content could contain @everyone, which must not ping the server.
        Assert.IsEmpty(body["allowed_mentions"]!["parse"]!.AsArray());
    }

    [TestMethod]
    public async Task DiscordLeavesOutTheUrlOfANotificationWithoutOne()
    {
        await using var server = await FakeHttpServer.StartAsync(testContext.CancellationToken);
        using var http = new HttpClient();

        await new DiscordSender(http).SendAsync(Discord(new Uri(server.BaseAddress, "api/webhooks/1/a")),
            Notification.Test("Deals server"), testContext.CancellationToken);

        var embed = JsonNode.Parse(server.Requests.Single().Body)!["embeds"]![0]!.AsObject();
        Assert.IsFalse(embed.ContainsKey("url"));
    }

    [TestMethod]
    public async Task DiscordShortensATitleAndDescriptionLongerThanItAllows()
    {
        await using var server = await FakeHttpServer.StartAsync(testContext.CancellationToken);
        using var http = new HttpClient();
        var notification = new Notification(NotificationKind.Change, new string('n', 300), new string('m', 5000), "https://example.com");

        await new DiscordSender(http).SendAsync(Discord(new Uri(server.BaseAddress, "api/webhooks/1/a")), notification,
            testContext.CancellationToken);

        var embed = JsonNode.Parse(server.Requests.Single().Body)!["embeds"]![0]!;
        var title = embed["title"]!.GetValue<string>();
        var description = embed["description"]!.GetValue<string>();
        Assert.HasCount(256, title);
        Assert.EndsWith("…", title);
        Assert.HasCount(4096, description);
        Assert.EndsWith("…", description);
    }

    [TestMethod]
    public async Task DiscordReportsARejectedWebhookWithoutRevealingItsToken()
    {
        await using var server = await FakeHttpServer.StartAsync(testContext.CancellationToken);
        server.Respond = context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return context.Response.WriteAsJsonAsync(new { message = "Unknown Webhook", code = 10015 }, context.RequestAborted);
        };
        using var http = new HttpClient();
        var notificationChannel = Discord(new Uri(server.BaseAddress, "api/webhooks/123/webhook-token"));

        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            new DiscordSender(http).SendAsync(notificationChannel, Change, testContext.CancellationToken));

        Assert.AreEqual("Discord returned HTTP 404 (Not Found): Unknown Webhook", error.Message);
    }

    [TestMethod]
    public async Task PushoverPostsTheMessageWithTheAppTokenAndUserKey()
    {
        await using var server = await FakeHttpServer.StartAsync(testContext.CancellationToken);
        server.Respond = context => context.Response.WriteAsJsonAsync(new { status = 1, request = "abc" }, context.RequestAborted);
        using var http = new HttpClient { BaseAddress = server.BaseAddress };

        await new PushoverSender(http).SendAsync(Pushover(), Change, testContext.CancellationToken);

        var request = server.Requests.Single();
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual("/1/messages.json", request.PathAndQuery);
        Assert.StartsWith("application/x-www-form-urlencoded", request.ContentType!);
        var form = Form(request.Body);
        Assert.AreEqual(AppToken, form["token"]);
        Assert.AreEqual(UserKey, form["user"]);
        Assert.AreEqual("Widget price changed", form["title"]);
        Assert.AreEqual("£20 → £19", form["message"]);
        Assert.AreEqual("https://example.com/widget", form["url"]);
        Assert.AreEqual("Open page", form["url_title"]);
    }

    [TestMethod]
    public async Task PushoverLeavesOutTheUrlOfANotificationWithoutOne()
    {
        await using var server = await FakeHttpServer.StartAsync(testContext.CancellationToken);
        server.Respond = context => context.Response.WriteAsJsonAsync(new { status = 1, request = "abc" }, context.RequestAborted);
        using var http = new HttpClient { BaseAddress = server.BaseAddress };

        await new PushoverSender(http).SendAsync(Pushover(), Notification.Test("Phone"), testContext.CancellationToken);

        var form = Form(server.Requests.Single().Body);
        Assert.IsFalse(form.ContainsKey("url"));
        Assert.IsFalse(form.ContainsKey("url_title"));
    }

    [TestMethod]
    public async Task PushoverShortensATitleAndMessageLongerThanItAllows()
    {
        await using var server = await FakeHttpServer.StartAsync(testContext.CancellationToken);
        server.Respond = context => context.Response.WriteAsJsonAsync(new { status = 1, request = "abc" }, context.RequestAborted);
        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        var notification = new Notification(NotificationKind.Change, new string('n', 300), new string('m', 2000), "https://example.com");

        await new PushoverSender(http).SendAsync(Pushover(), notification, testContext.CancellationToken);

        var form = Form(server.Requests.Single().Body);
        Assert.HasCount(250, form["title"]);
        Assert.EndsWith("…", form["title"]);
        Assert.HasCount(1024, form["message"]);
        Assert.EndsWith("…", form["message"]);
    }

    [TestMethod]
    public async Task PushoverReportsItsErrorsWithoutRevealingTheAppToken()
    {
        await using var server = await FakeHttpServer.StartAsync(testContext.CancellationToken);
        server.Respond = context =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return context.Response.WriteAsJsonAsync(new { token = "invalid", errors = PushoverErrors, status = 0 },
                context.RequestAborted);
        };
        using var http = new HttpClient { BaseAddress = server.BaseAddress };

        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            new PushoverSender(http).SendAsync(Pushover(), Change, testContext.CancellationToken));

        Assert.AreEqual("Pushover returned HTTP 400 (Bad Request): application token is invalid; user identifier is not valid",
            error.Message);
    }

    [TestMethod]
    public async Task ASenderKeepsTheStatusWhenTheErrorResponseIsNotJson()
    {
        await using var server = await FakeHttpServer.StartAsync(testContext.CancellationToken);
        server.Respond = context =>
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return context.Response.WriteAsync("<html>Bad gateway</html>", context.RequestAborted);
        };
        using var http = new HttpClient { BaseAddress = server.BaseAddress };

        var discord = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new DiscordSender(http)
            .SendAsync(Discord(new Uri(server.BaseAddress, "api/webhooks/1/a")), Change, testContext.CancellationToken));
        var pushover = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            new PushoverSender(http).SendAsync(Pushover(), Change, testContext.CancellationToken));

        Assert.AreEqual("Discord returned HTTP 502 (Bad Gateway).", discord.Message);
        Assert.AreEqual("Pushover returned HTTP 502 (Bad Gateway).", pushover.Message);
    }

    private static NotificationChannel Discord(Uri webhookUrl) =>
        new() { Name = "Deals server", Kind = NotificationChannelKind.Discord, WebhookUrl = webhookUrl.ToString() };

    private static NotificationChannel Pushover() =>
        new() { Name = "Phone", Kind = NotificationChannelKind.Pushover, UserKey = UserKey, AppToken = AppToken };

    private static Dictionary<string, string> Form(string body) =>
        QueryHelpers.ParseQuery(body).ToDictionary(field => field.Key, field => field.Value.ToString());
}
