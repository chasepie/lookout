using System.Net;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.UnitTests;

/// <summary>
/// What the Pushover client sends to Pushover's API, with Pushover faked at its HTTP boundary.
/// </summary>
[TestClass]
public sealed class PushoverHttpClientTests
{
    private static readonly PushoverDestination Destination = new(
        ApplicationToken: "app-token", UserKey: "user-key", Priority: 0, Sound: null, RetrySeconds: 60, ExpireSeconds: 3600);

    private static readonly Notification Notification = new(Title: "Shoe stock", Message: "Sold Out → In Stock");

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task SendsTheMessageWithItsCredentialsToPushoversMessagesEndpoint()
    {
        var pushover = new FakePushover();

        await SendAsync(pushover, Destination, Notification);

        var request = pushover.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(new Uri("https://api.pushover.net/1/messages.json"), request.Uri);
        Assert.AreEqual("app-token", request.Fields["token"]);
        Assert.AreEqual("user-key", request.Fields["user"]);
        Assert.AreEqual("Shoe stock", request.Fields["title"]);
        Assert.AreEqual("Sold Out → In Stock", request.Fields["message"]);
        Assert.AreEqual("0", request.Fields["priority"]);
    }

    [TestMethod]
    public async Task AnEmergencyCarriesHowOftenAndHowLongItRepeats()
    {
        var pushover = new FakePushover();

        await SendAsync(pushover, Destination with { Priority = 2, RetrySeconds = 30, ExpireSeconds = 10800 }, Notification);

        var fields = pushover.Requests.Single().Fields;
        Assert.AreEqual("2", fields["priority"]);
        Assert.AreEqual("30", fields["retry"]);
        Assert.AreEqual("10800", fields["expire"]);
    }

    [TestMethod]
    public async Task OnlyAnEmergencyCarriesRetryAndExpire()
    {
        var pushover = new FakePushover();

        await SendAsync(pushover, Destination with { Priority = 1 }, Notification);

        var fields = pushover.Requests.Single().Fields;
        Assert.IsFalse(fields.ContainsKey("retry"));
        Assert.IsFalse(fields.ContainsKey("expire"));
    }

    [TestMethod]
    public async Task SendsTheSoundAndTheLinkToTheRun()
    {
        var pushover = new FakePushover();

        await SendAsync(
            pushover,
            Destination with { Sound = "cashregister" },
            Notification with { Url = new Uri("https://lookout.example/watches/3/runs/41") });

        var fields = pushover.Requests.Single().Fields;
        Assert.AreEqual("cashregister", fields["sound"]);
        Assert.AreEqual("https://lookout.example/watches/3/runs/41", fields["url"]);
    }

    [TestMethod]
    public async Task LeavesOutTheSoundAndLinkWhenThereAreNone()
    {
        var pushover = new FakePushover();

        await SendAsync(pushover, Destination with { Sound = null }, Notification with { Url = null });

        var fields = pushover.Requests.Single().Fields;
        Assert.IsFalse(fields.ContainsKey("sound"));
        Assert.IsFalse(fields.ContainsKey("url"));
    }

    [TestMethod]
    [DataRow("image/png", "screenshot.png")]
    [DataRow("image/jpeg", "screenshot.jpg")]
    public async Task AttachesTheScreenshot(string mediaType, string fileName)
    {
        var pushover = new FakePushover();
        byte[] screenshot = [1, 2, 3];

        await SendAsync(pushover, Destination, Notification with { Screenshot = new(screenshot, mediaType) });

        var attachment = pushover.Requests.Single().Files["attachment"];
        Assert.AreSequenceEqual(screenshot, attachment.Bytes);
        Assert.AreEqual(mediaType, attachment.Type?.MediaType);
        Assert.AreEqual(fileName, attachment.FileName);
    }

    [TestMethod]
    public async Task LeavesOutAScreenshotOverPushoversLimitAndSaysSo()
    {
        var pushover = new FakePushover();
        var screenshot = new byte[5_242_880 + 1];

        await SendAsync(pushover, Destination, Notification with { Screenshot = new(screenshot, "image/png") });

        var request = pushover.Requests.Single();
        Assert.IsEmpty(request.Files);
        Assert.AreEqual(
            "Sold Out → In Stock\n\n(The screenshot was left out: it's over Pushover's 5 MB limit.)",
            request.Fields["message"]);
    }

    [TestMethod]
    public async Task AttachesAScreenshotOfExactlyPushoversLimit()
    {
        var pushover = new FakePushover();

        await SendAsync(pushover, Destination, Notification with { Screenshot = new(new byte[5_242_880], "image/png") });

        Assert.IsTrue(pushover.Requests.Single().Files.ContainsKey("attachment"));
    }

    [TestMethod]
    public async Task CutsALongTitleAndMessageToPushoversLimits()
    {
        var pushover = new FakePushover();

        await SendAsync(pushover, Destination, new Notification(new string('t', 300), new string('m', 2000)));

        var fields = pushover.Requests.Single().Fields;
        Assert.AreEqual(new string('t', 249) + "…", fields["title"]);
        Assert.AreEqual(new string('m', 1023) + "…", fields["message"]);
    }

    [TestMethod]
    public async Task KeepsTheNoteAboutALeftOutScreenshotWhenCuttingTheMessage()
    {
        var pushover = new FakePushover();

        await SendAsync(
            pushover,
            Destination,
            new Notification("Shoe stock", new string('m', 2000), Screenshot: new(new byte[5_242_881], "image/png")));

        var message = pushover.Requests.Single().Fields["message"];
        Assert.HasCount(1024, message);
        Assert.EndsWith("…\n\n(The screenshot was left out: it's over Pushover's 5 MB limit.)", message);
    }

    [TestMethod]
    public async Task DoesNotCutACharacterInHalf()
    {
        var pushover = new FakePushover();

        // Each 🙂 is two UTF-16 code units, so the 249th, where the cut falls before the "…", is the first half of one
        await SendAsync(pushover, Destination, Notification with { Title = string.Concat(Enumerable.Repeat("🙂", 200)) });

        var title = pushover.Requests.Single().Fields["title"];
        Assert.AreEqual(string.Concat(Enumerable.Repeat("🙂", 124)) + "…", title);
    }

    [TestMethod]
    public async Task ARefusedMessageFailsWithPushoversReasonAndIsNotRetried()
    {
        var pushover = new FakePushover(
            HttpStatusCode.BadRequest,
            """{"user":"invalid","errors":["user identifier is not a valid user, group, or subscribed user key"],"status":0,"request":"5042853c-402d-4a18-abcb-168734a801de"}""");

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => SendAsync(pushover, Destination, Notification));

        Assert.IsFalse(ex.Retryable);
        Assert.AreEqual(
            "Pushover refused the notification (400): user identifier is not a valid user, group, or subscribed user key",
            ex.Message);
    }

    [TestMethod]
    public async Task UsingUpTheMonthlyLimitIsNotRetried()
    {
        var pushover = new FakePushover((HttpStatusCode)429, """{"status":0,"errors":["application has exceeded its monthly limit"]}""");

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => SendAsync(pushover, Destination, Notification));

        Assert.IsFalse(ex.Retryable);
    }

    [TestMethod]
    public async Task AServerErrorCanBeRetried()
    {
        var pushover = new FakePushover(HttpStatusCode.ServiceUnavailable, "<html>Service Unavailable</html>");

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => SendAsync(pushover, Destination, Notification));

        Assert.IsTrue(ex.Retryable);
        Assert.AreEqual("Pushover failed to take the notification (503).", ex.Message);
    }

    [TestMethod]
    public async Task NotReachingPushoverCanBeRetried()
    {
        var pushover = new FakePushover(fail: new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => SendAsync(pushover, Destination, Notification));

        Assert.IsTrue(ex.Retryable);
        Assert.AreEqual("Couldn't reach Pushover: Connection refused", ex.Message);
    }

    private static async Task SendAsync(FakePushover pushover, PushoverDestination destination, Notification notification)
    {
        var services = new ServiceCollection();
        services.AddHttpClient<PushoverHttpClient>(PushoverHttpClient.Configure)
            .ConfigurePrimaryHttpMessageHandler(() => pushover);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<PushoverHttpClient>().SendAsync(destination, notification, CancellationToken.None);
    }
}
