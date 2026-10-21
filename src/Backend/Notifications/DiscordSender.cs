using System.Text.Json;
using System.Text.Json.Serialization;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>Posts notifications to a Discord webhook, whose URL includes the token that authorises the post.</summary>
public sealed class DiscordSender(HttpClient client) : IChannelSender
{
    // Discord's limits on an embed's title and description.
    private const int MaxTitleLength = 256;
    private const int MaxDescriptionLength = 4096;

    public ChannelKind Kind => ChannelKind.Discord;

    public async Task SendAsync(Channel channel, Notification notification, CancellationToken cancellationToken)
    {
        var embed = new Embed(
                Title: Notification.Fit(notification.Title, MaxTitleLength),
                Description: Notification.Fit(notification.Message, MaxDescriptionLength),
                Url: notification.Url);

        var message = new WebhookMessage(
            Username: "Lookout",
            Embeds: [embed],
            // Content from a page could mention @everyone or a role; nobody should be pinged by it.
            AllowedMentions: new AllowedMentions([]));

        using var response = await client.PostAsJsonAsync(channel.WebhookUrl, message, cancellationToken);
        await ChannelResponses.EnsureAcceptedAsync(response, "Discord", body =>
            body.TryGetProperty("message", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null,
            cancellationToken);
    }

    private sealed record WebhookMessage(
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("embeds")] IReadOnlyList<Embed> Embeds,
        [property: JsonPropertyName("allowed_mentions")] AllowedMentions AllowedMentions);

    private sealed record Embed(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url);

    private sealed record AllowedMentions([property: JsonPropertyName("parse")] IReadOnlyList<string> Parse);
}
