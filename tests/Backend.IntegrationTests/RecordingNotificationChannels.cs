using System.Collections.Concurrent;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>A notification a Notification channel was sent, with the Notification channel's name and kind.</summary>
public sealed record SentNotification(string NotificationChannelName, NotificationChannelKind NotificationChannelKind, Notification Notification);

/// <summary>Records the notifications sent to every Notification channel, of any kind, instead of delivering them.</summary>
public sealed class RecordingNotificationChannels
{
    public ConcurrentQueue<SentNotification> Sent { get; } = new();
    /// <summary>Runs before each notification is recorded, so a test can make sending fail.</summary>
    public Func<NotificationChannel, Notification, Task>? OnSend { get; set; }

    public IEnumerable<INotificationChannelSender> Senders() => Enum.GetValues<NotificationChannelKind>().Select(kind => new Sender(this, kind));

    private sealed class Sender(RecordingNotificationChannels notificationChannels, NotificationChannelKind kind) : INotificationChannelSender
    {
        public NotificationChannelKind Kind => kind;

        public async Task SendAsync(NotificationChannel notificationChannel, Notification notification, CancellationToken cancellationToken)
        {
            if (notificationChannels.OnSend is { } onSend)
            {
                await onSend(notificationChannel, notification);
            }
            notificationChannels.Sent.Enqueue(new SentNotification(notificationChannel.Name, notificationChannel.Kind, notification));
        }
    }
}
