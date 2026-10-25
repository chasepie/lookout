using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>Delivers notifications to Channels of one kind.</summary>
public interface IChannelSender
{
    ChannelKind Kind { get; }

    /// <summary>Sends the notification, throwing if the Channel's service didn't accept it.</summary>
    Task SendAsync(Channel channel, Notification notification, CancellationToken cancellationToken);
}
