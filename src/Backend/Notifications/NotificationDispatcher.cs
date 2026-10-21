using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>Sends notifications to Channels through the sender for each Channel's kind.</summary>
public sealed partial class NotificationDispatcher(
    LookoutDbContext database,
    IEnumerable<IChannelSender> senders,
    ILogger<NotificationDispatcher> logger)
{
    private readonly Dictionary<ChannelKind, IChannelSender> _senders = senders.ToDictionary(sender => sender.Kind);

    /// <summary>Sends one notification to one Channel, throwing if its service didn't accept it.</summary>
    public Task SendAsync(Channel channel, Notification notification, CancellationToken cancellationToken) =>
        _senders[channel.Kind].SendAsync(channel, notification, cancellationToken);

    /// <summary>
    /// Sends a Run's notifications to each Channel the Watch notifies, in order. A Channel that fails is logged and
    /// skipped, so neither the Run nor the other Channels depend on it.
    /// </summary>
    public async Task NotifyAsync(Guid watchId, IReadOnlyList<Notification> notifications, CancellationToken cancellationToken)
    {
        if (notifications.Count == 0)
        {
            return;
        }

        var channels = await database.Channels.AsNoTracking()
            .Where(channel => channel.Watches.Any(watch => watch.Id == watchId))
            .ToListAsync(cancellationToken);
        await Task.WhenAll(channels.Select(async channel =>
        {
            foreach (var notification in notifications)
            {
                try
                {
                    await SendAsync(channel, notification, cancellationToken);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    LogSendFailed(logger, error, notification.Kind, channel.Id, watchId);
                }
            }
        }));
    }

    [LoggerMessage(LogLevel.Warning, "Could not send a {Kind} notification to Channel {ChannelId} for Watch {WatchId}.")]
    private static partial void LogSendFailed(ILogger logger, Exception error, NotificationKind kind, Guid channelId, Guid watchId);
}
