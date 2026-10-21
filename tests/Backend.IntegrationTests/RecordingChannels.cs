using System.Collections.Concurrent;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>A notification a Channel was sent, with the Channel's name and kind.</summary>
public sealed record SentNotification(string ChannelName, ChannelKind ChannelKind, Notification Notification);

/// <summary>Records the notifications sent to every Channel, of any kind, instead of delivering them.</summary>
public sealed class RecordingChannels
{
    public ConcurrentQueue<SentNotification> Sent { get; } = new();
    /// <summary>Runs before each notification is recorded, so a test can make sending fail.</summary>
    public Func<Channel, Notification, Task>? OnSend { get; set; }

    public IEnumerable<IChannelSender> Senders() => Enum.GetValues<ChannelKind>().Select(kind => new Sender(this, kind));

    private sealed class Sender(RecordingChannels channels, ChannelKind kind) : IChannelSender
    {
        public ChannelKind Kind => kind;

        public async Task SendAsync(Channel channel, Notification notification, CancellationToken cancellationToken)
        {
            if (channels.OnSend is { } onSend)
            {
                await onSend(channel, notification);
            }
            channels.Sent.Enqueue(new SentNotification(channel.Name, channel.Kind, notification));
        }
    }
}
