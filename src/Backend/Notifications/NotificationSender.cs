using System.Threading.Channels;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>
/// A notification on its way to one notification channel.
/// </summary>
/// <param name="ChannelName">The notification channel's name, for the logs.</param>
public sealed record NotificationDelivery(
    string ChannelName,
    PushoverDestination Destination,
    Notification Notification);

/// <summary>
/// Sends notifications in the background, so a run doesn't wait on a notification channel's service, and tries again
/// when the service might take one later. Notifications still waiting are lost if the Backend stops.
/// </summary>
public sealed partial class NotificationSender(
    IServiceProvider services,
    TimeProvider timeProvider,
    ILogger<NotificationSender> logger) : BackgroundService
{
    /// <summary>
    /// How long to wait before each attempt after the first, so a notification gets three over about five minutes.
    /// Pushover asks for at least five seconds.
    /// </summary>
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(4)];

    private readonly Channel<NotificationDelivery> _deliveries = Channel.CreateUnbounded<NotificationDelivery>();

    // Pushover asks for requests one at a time. Never disposed, since it needs no disposing without a wait handle, and a
    // delivery still running after a shutdown timed out may yet release it.
    private readonly SemaphoreSlim _sending = new(1, 1);

    public void Enqueue(NotificationDelivery delivery)
    {
        _deliveries.Writer.TryWrite(delivery);
    }

    /// <summary>
    /// Sends a notification right away, without retrying, in turn with the ones being sent in the background.
    /// </summary>
    /// <exception cref="NotificationDeliveryException">The channel's service didn't take it.</exception>
    public async Task SendNowAsync(
        PushoverDestination destination,
        Notification notification,
        CancellationToken cancellationToken)
    {
        await _sending.WaitAsync(cancellationToken);
        try
        {
            var pushover = services.GetRequiredService<PushoverHttpClient>();
            await pushover.SendAsync(destination, notification, cancellationToken);
        }
        finally
        {
            _sending.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var deliveries = new List<Task>();
        try
        {
            await foreach (var delivery in _deliveries.Reader.ReadAllAsync(stoppingToken))
            {
                // Each on its own, so one waiting to try again doesn't hold up the others
                deliveries.RemoveAll(task => task.IsCompleted);
                deliveries.Add(DeliverAsync(delivery, stoppingToken));
            }
        }
        finally
        {
            await Task.WhenAll(deliveries);
        }
    }

    private async Task DeliverAsync(NotificationDelivery delivery, CancellationToken stoppingToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await SendNowAsync(delivery.Destination, delivery.Notification, stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (NotificationDeliveryException ex) when (ex.Retryable && attempt < RetryDelays.Length)
            {
                var delay = RetryDelays[attempt];

                // Started before logging, so whoever sees the log can count on the wait having begun
                var wait = Task.Delay(delay, timeProvider, stoppingToken);
                LogRetrying(ex, delivery.ChannelName, delay, ex.Message);
                try
                {
                    await wait;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                LogGaveUp(ex, delivery.ChannelName, ex.Message);
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Couldn't send a notification to {ChannelName}, trying again in {RetryDelay}: {Reason}")]
    private partial void LogRetrying(Exception exception, string channelName, TimeSpan retryDelay, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gave up sending a notification to {ChannelName}: {Reason}")]
    private partial void LogGaveUp(Exception exception, string channelName, string reason);
}
