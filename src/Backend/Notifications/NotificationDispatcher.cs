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
    /// Sends a Run's notifications to each Channel the Watch notifies, in order. Failures are logged, never thrown: the
    /// Run is already recorded, and a Channel that fails is skipped, so the other Channels don't depend on it.
    /// </summary>
    public async Task NotifyAsync(Guid watchId, IReadOnlyList<Notification> notifications, CancellationToken cancellationToken)
    {
        if (notifications.Count == 0)
        {
            return;
        }

        List<Channel> channels;
        try
        {
            channels = await database.Channels.AsNoTracking()
                .Where(channel => channel.Watches.Any(watch => watch.Id == watchId))
                .ToListAsync(cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            LogChannelsUnavailable(logger, error, watchId);
            return;
        }

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

    [LoggerMessage(LogLevel.Error, "Could not read which Channels Watch {WatchId} notifies, so its Run's notifications were not sent.")]
    private static partial void LogChannelsUnavailable(ILogger logger, Exception error, Guid watchId);

    [LoggerMessage(LogLevel.Warning, "Could not send a {Kind} notification to Channel {ChannelId} for Watch {WatchId}.")]
    private static partial void LogSendFailed(ILogger logger, Exception error, NotificationKind kind, Guid channelId, Guid watchId);
}
