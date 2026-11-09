namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>
/// A notification channel's service didn't take a notification.
/// </summary>
/// <param name="retryable">
/// Whether sending it again later might work, as after a server error, rather than being refused for what was sent.
/// </param>
public sealed class NotificationDeliveryException(string message, bool retryable, Exception? innerException = null)
    : Exception(message, innerException)
{
    public bool Retryable { get; } = retryable;
}
