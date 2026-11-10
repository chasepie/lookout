using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ChasePie.Lookout.Core;

/// <summary>
/// A watch's choice to send some of its events to a notification channel, and how. A watch may have several, even to
/// the same channel, but may send each event to a channel only once.
/// </summary>
public class SubscriptionDto
{
    public int NotificationChannelId { get; set; }

    /// <summary>
    /// The events sent to the channel; at least one.
    /// </summary>
    public List<WatchEvent> Events { get; set; } = [];

    /// <summary>
    /// How the notifications are sent, when the channel is a Pushover one.
    /// </summary>
    public PushoverSubscriptionDto? Pushover { get; set; }
}

/// <summary>
/// How a subscription's notifications are sent through a Pushover channel.
/// </summary>
public class PushoverSubscriptionDto
{
    /// <summary>
    /// From -2, the quietest, to 2, an emergency that repeats as the channel says until acknowledged.
    /// </summary>
    [Range(-2, 2)]
    public int Priority { get; set; }

    /// <summary>
    /// The name of a Pushover sound, or <see langword="null"/> for the user's default.
    /// </summary>
    [MaxLength(100)]
    public string? Sound { get; set; }
}

/// <inheritdoc cref="SubscriptionDto"/>
public class Subscription
{
    public int Id { get; set; }

    public int WatchId { get; set; }

    [JsonIgnore]
    public Watch Watch { get; set; } = null!;

    public int NotificationChannelId { get; set; }

    [JsonIgnore]
    public NotificationChannel NotificationChannel { get; set; } = null!;

    public List<WatchEvent> Events { get; set; } = [];

    public PushoverSubscriptionOptions? Pushover { get; set; }
}

/// <inheritdoc cref="PushoverSubscriptionDto"/>
public class PushoverSubscriptionOptions
{
    public int Priority { get; set; }

    public string? Sound { get; set; }
}
