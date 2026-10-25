using System.Text.Json;
using System.Text.Json.Serialization;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>Posts notifications to a Discord webhook, whose URL includes the token that authorises the post.</summary>
public sealed class DiscordSender(HttpClient client) : INotificationChannelSender
{
    // Discord's limits on an embed's title and description.
    private const int MaxTitleLength = 256;
    private const int MaxDescriptionLength = 4096;

    public NotificationChannelKind Kind => NotificationChannelKind.Discord;

    public async Task SendAsync(
        NotificationChannel notificationChannel,
        Notification notification,
        CancellationToken cancellationToken)
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

        using var response = await client.PostAsJsonAsync(notificationChannel.WebhookUrl, message, cancellationToken);
        await NotificationChannelResponses.EnsureAcceptedAsync(response, "Discord",
            body =>
            {
                if (body.TryGetProperty("message", out var reason)
                    && reason.ValueKind == JsonValueKind.String)
                {
                    return reason.GetString();
                }

                return null;
            },
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

    private sealed record AllowedMentions(
        [property: JsonPropertyName("parse")] IReadOnlyList<string> Parse);
}
