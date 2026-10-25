namespace ChasePie.Lookout.Model;

/// <summary>A Watch's choice of one Notification channel to notify.</summary>
public sealed class WatchNotificationChannel
{
    public Guid WatchId { get; set; }
    public Guid NotificationChannelId { get; set; }
}
