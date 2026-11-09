using System.ComponentModel.DataAnnotations;

namespace ChasePie.Lookout.Core;

/// <summary>
/// The service a notification channel sends through.
/// </summary>
public enum NotificationService
{
    Pushover,
}

/// <summary>
/// A notification channel as the Owner sets it up. Its credentials are write-only: the API takes them but never
/// returns them (<c>docs/adr/0005-notification-channel-credentials-in-database.md</c>).
/// </summary>
public class NotificationChannelDto : IValidatableObject
{
    [Required]
    public required string Name { get; set; }

    public NotificationService Service { get; set; }

    /// <summary>
    /// The channel's settings when <see cref="Service"/> is <see cref="NotificationService.Pushover"/>.
    /// </summary>
    public PushoverChannelDto? Pushover { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Service == NotificationService.Pushover && Pushover is null)
        {
            yield return new ValidationResult(
                $"{nameof(Pushover)} is required for a Pushover channel.", [nameof(Pushover)]);
        }
    }
}

/// <summary>
/// A Pushover channel's settings. The token and user key may be left out when editing a channel, to keep them.
/// </summary>
public class PushoverChannelDto
{
    /// <summary>
    /// The token of the Pushover application that sends.
    /// </summary>
    public string? ApplicationToken { get; set; }

    /// <summary>
    /// The Pushover user or group key that receives.
    /// </summary>
    public string? UserKey { get; set; }

    /// <summary>
    /// How often, in seconds, an emergency notification (priority 2) repeats until acknowledged. Pushover's minimum is 30.
    /// </summary>
    [Range(30, PushoverChannelSettings.MaxExpireSeconds)]
    public int RetrySeconds { get; set; } = 60;

    /// <summary>
    /// How long, in seconds, an emergency notification keeps repeating. Pushover's maximum is 3 hours.
    /// </summary>
    [Range(30, PushoverChannelSettings.MaxExpireSeconds)]
    public int ExpireSeconds { get; set; } = 3600;
}

/// <summary>
/// Somewhere notifications go. There may be any number, each used by any number of watches.
/// </summary>
public class NotificationChannel
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public NotificationService Service { get; set; }

    public PushoverChannelSettings? Pushover { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public ICollection<Subscription> Subscriptions { get; set; } = [];
}

/// <summary>
/// A Pushover channel's settings, as stored.
/// </summary>
public class PushoverChannelSettings
{
    public const int MaxExpireSeconds = 3 * 60 * 60;

    public required string ApplicationToken { get; set; }

    public required string UserKey { get; set; }

    public int RetrySeconds { get; set; }

    public int ExpireSeconds { get; set; }
}

/// <summary>
/// A notification channel as the API returns it: without its credentials.
/// </summary>
/// <param name="Watches">The watches that send it notifications, by name.</param>
public sealed record NotificationChannelSummary(
    int Id,
    string Name,
    NotificationService Service,
    PushoverChannelSummary? Pushover,
    IReadOnlyList<WatchReference> Watches);

/// <summary>
/// A watch, by its name.
/// </summary>
public sealed record WatchReference(int Id, string Name);

/// <summary>
/// A Pushover channel's settings, without its token and user key.
/// </summary>
public sealed record PushoverChannelSummary(int RetrySeconds, int ExpireSeconds);
