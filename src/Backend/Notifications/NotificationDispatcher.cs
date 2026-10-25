using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>Sends notifications to Notification channels through the sender for each Notification channel's kind.</summary>
public sealed partial class NotificationDispatcher(
    LookoutDbContext database,
    IEnumerable<INotificationChannelSender> senders,
    ILogger<NotificationDispatcher> logger)
{
    private readonly Dictionary<NotificationChannelKind, INotificationChannelSender> _senders = senders.ToDictionary(sender => sender.Kind);

    /// <summary>Sends one notification to one Notification channel, throwing if its service didn't accept it.</summary>
    public Task SendAsync(NotificationChannel notificationChannel, Notification notification, CancellationToken cancellationToken) =>
        _senders[notificationChannel.Kind].SendAsync(notificationChannel, notification, cancellationToken);

    /// <summary>
    /// Sends a Run's notifications to each Notification channel the Watch notifies, in order. Failures are logged, never
    /// thrown: the Run is already recorded, and a Notification channel that fails is skipped, so the other Notification
    /// channels don't depend on it.
    /// </summary>
    public async Task NotifyAsync(Guid watchId, IReadOnlyList<Notification> notifications, CancellationToken cancellationToken)
    {
        if (notifications.Count == 0)
        {
            return;
        }

        List<NotificationChannel> notificationChannels;
        try
        {
            notificationChannels = await database.NotificationChannels.AsNoTracking()
                .Where(notificationChannel => notificationChannel.Watches.Any(watch => watch.Id == watchId))
                .ToListAsync(cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            LogNotificationChannelsUnavailable(logger, error, watchId);
            return;
        }

        await Task.WhenAll(notificationChannels.Select(async notificationChannel =>
        {
            foreach (var notification in notifications)
            {
                try
                {
                    await SendAsync(notificationChannel, notification, cancellationToken);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    LogSendFailed(logger, error, notification.Kind, notificationChannel.Id, watchId);
                }
            }
        }));
    }

    [LoggerMessage(LogLevel.Error, "Could not read which Notification channels Watch {WatchId} notifies, so its Run's notifications were not sent.")]
    private static partial void LogNotificationChannelsUnavailable(ILogger logger, Exception error, Guid watchId);

    [LoggerMessage(LogLevel.Warning, "Could not send a {Kind} notification to Notification channel {NotificationChannelId} for Watch {WatchId}.")]
    private static partial void LogSendFailed(ILogger logger, Exception error, NotificationKind kind, Guid notificationChannelId, Guid watchId);
}
