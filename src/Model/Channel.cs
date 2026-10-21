using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChasePie.Lookout.Model;

[JsonConverter(typeof(ChannelKindJsonConverter))]
public enum ChannelKind
{
    Discord,
    Pushover
}

public sealed class ChannelKindJsonConverter() : JsonStringEnumConverter<ChannelKind>(JsonNamingPolicy.CamelCase, false);

/// <summary>
/// A destination Lookout sends notifications to, configured once and chosen per Watch. Only the fields its
/// <see cref="Kind"/> uses are set. Its secrets stay in the backend: they are never returned by the API or given to Scripts.
/// </summary>
public sealed class Channel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public ChannelKind Kind { get; set; }
    /// <summary>For Discord, the webhook URL, which includes the token that lets anyone post to it.</summary>
    public string? WebhookUrl { get; set; }
    /// <summary>For Pushover, the user or group key that receives notifications.</summary>
    public string? UserKey { get; set; }
    /// <summary>For Pushover, the application's API token.</summary>
    public string? AppToken { get; set; }
    public ICollection<Watch> Watches { get; set; } = [];
}
