using System.Net;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace ChasePie.Lookout.Backend.UnitTests;

/// <summary>
/// How notifications waiting to be sent get to Pushover, with Pushover faked at its HTTP boundary and time faked.
/// </summary>
[TestClass]
public sealed class NotificationSenderTests
{
    private static readonly PushoverDestination Destination = new(
        ApplicationToken: "app-token", UserKey: "user-key", Priority: 0, Sound: null, RetrySeconds: 60, ExpireSeconds: 3600);

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task SendsANotification()
    {
        var pushover = new FakePushover();
        await using var sender = await StartSenderAsync(pushover);

        sender.Enqueue(Delivery("Sold Out → In Stock"));

        await pushover.WaitForRequestsAsync(1, Ct);
        Assert.AreEqual("Sold Out → In Stock", pushover.Requests.Single().Fields["message"]);
    }

    [TestMethod]
    public async Task RetriesAfterAMinuteThenFourMoreThenGivesUp()
    {
        var pushover = new FakePushover(HttpStatusCode.ServiceUnavailable);
        await using var sender = await StartSenderAsync(pushover);

        sender.Enqueue(Delivery("Sold Out → In Stock"));

        await pushover.WaitForRequestsAsync(1, Ct);
        await sender.WaitForLogAsync(LogLevel.Warning, Ct);
        Time.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        Assert.HasCount(1, pushover.Requests);

        Time.Advance(TimeSpan.FromSeconds(1));
        await pushover.WaitForRequestsAsync(2, Ct);
        await sender.WaitForLogAsync(LogLevel.Warning, Ct, count: 2);
        Time.Advance(TimeSpan.FromMinutes(4));
        await pushover.WaitForRequestsAsync(3, Ct);

        var error = await sender.WaitForLogAsync(LogLevel.Error, Ct);
        Assert.AreEqual(
            "Gave up sending a notification to Phone: Pushover failed to take the notification (503).", error.Message);
        Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        Assert.HasCount(3, pushover.Requests);
    }

    [TestMethod]
    public async Task GivesUpRightAwayOnANotificationPushoverRefuses()
    {
        var pushover = new FakePushover(HttpStatusCode.BadRequest, """{"status":0,"errors":["application token is invalid"]}""");
        await using var sender = await StartSenderAsync(pushover);

        sender.Enqueue(Delivery("Sold Out → In Stock"));

        var error = await sender.WaitForLogAsync(LogLevel.Error, Ct);
        Assert.AreEqual(
            "Gave up sending a notification to Phone: Pushover refused the notification (400): application token is invalid",
            error.Message);
        Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        Assert.HasCount(1, pushover.Requests);
    }

    [TestMethod]
    public async Task ANotificationWaitingToBeTriedAgainDoesNotHoldUpOthers()
    {
        var pushover = new FakePushover(HttpStatusCode.ServiceUnavailable);
        await using var sender = await StartSenderAsync(pushover);
        sender.Enqueue(Delivery("First"));
        await sender.WaitForLogAsync(LogLevel.Warning, Ct);

        pushover.Status = HttpStatusCode.OK;
        sender.Enqueue(Delivery("Second"));

        await pushover.WaitForRequestsAsync(2, Ct);
        Assert.AreEqual("Second", pushover.Requests[1].Fields["message"]);
    }

    [TestMethod]
    public async Task StopsTryingOnceANotificationIsSent()
    {
        var pushover = new FakePushover(HttpStatusCode.ServiceUnavailable);
        await using var sender = await StartSenderAsync(pushover);
        sender.Enqueue(Delivery("Sold Out → In Stock"));
        await sender.WaitForLogAsync(LogLevel.Warning, Ct);

        pushover.Status = HttpStatusCode.OK;
        Time.Advance(TimeSpan.FromMinutes(1));
        await pushover.WaitForRequestsAsync(2, Ct);
        Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);

        Assert.HasCount(2, pushover.Requests);
        Assert.DoesNotContain(r => r.Level == LogLevel.Error, sender.Logs.GetSnapshot());
    }

    private static NotificationDelivery Delivery(string message) =>
        new("Phone", Destination, new Notification("Shoe stock", message));

    private async Task<RunningSender> StartSenderAsync(FakePushover pushover)
    {
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(Time)
            .AddLogging(logging => logging.AddFakeLogging())
            .AddSingleton<NotificationSender>();
        services.AddHttpClient<PushoverHttpClient>(PushoverHttpClient.Configure)
            .ConfigurePrimaryHttpMessageHandler(() => pushover);
        var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<NotificationSender>();
        await ((IHostedService)queue).StartAsync(Ct);
        return new RunningSender(provider, queue);
    }

    private FakeTimeProvider Time { get; } = new();

    /// <summary>
    /// A started sender, stopped with its services on disposal.
    /// </summary>
    private sealed class RunningSender(ServiceProvider provider, NotificationSender queue) : IAsyncDisposable
    {
        public FakeLogCollector Logs => provider.GetFakeLogCollector();

        public void Enqueue(NotificationDelivery delivery) => queue.Enqueue(delivery);

        /// <summary>
        /// Waits, for up to 10 seconds of real time, until at least <paramref name="count"/> records of
        /// <paramref name="level"/> have been logged, and returns the last of them.
        /// </summary>
        public async Task<FakeLogRecord> WaitForLogAsync(LogLevel level, CancellationToken cancellationToken, int count = 1)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                var records = Logs.GetSnapshot().Where(r => r.Level == level).ToList();
                if (records.Count >= count)
                {
                    return records[count - 1];
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)queue).StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }
}
