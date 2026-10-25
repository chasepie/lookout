using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>Delivers notifications to Notification channels of one kind.</summary>
public interface INotificationChannelSender
{
    NotificationChannelKind Kind { get; }

    /// <summary>Sends the notification, throwing if the Notification channel's service didn't accept it.</summary>
    Task SendAsync(NotificationChannel notificationChannel, Notification notification, CancellationToken cancellationToken);
}
