using System.Text.Json;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>Sends notifications through Pushover's Message API, authorised by the Channel's app token and user key.</summary>
public sealed class PushoverSender(HttpClient client) : IChannelSender
{
    public const string DefaultBaseUrl = "https://api.pushover.net/";

    // Pushover's limits on a message's title, message and supplementary URL.
    private const int MaxTitleLength = 250;
    private const int MaxMessageLength = 1024;
    private const int MaxUrlLength = 512;

    public ChannelKind Kind => ChannelKind.Pushover;

    public async Task SendAsync(Channel channel, Notification notification, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["token"] = channel.AppToken!,
            ["user"] = channel.UserKey!,
            ["title"] = Notification.Fit(notification.Title, MaxTitleLength),
            ["message"] = Notification.Fit(notification.Message, MaxMessageLength)
        };

        // A link that doesn't fit would be broken, so it's left out rather than shortened.
        if (notification.Url is { Length: <= MaxUrlLength } url)
        {
            fields["url"] = url;
            fields["url_title"] = "Open page";
        }

        using var content = new FormUrlEncodedContent(fields);
        var uri = new Uri("1/messages.json", UriKind.Relative);
        using var response = await client.PostAsync(uri, content, cancellationToken);
        await ChannelResponses.EnsureAcceptedAsync(response, "Pushover",
            body =>
            {
                if (body.TryGetProperty("errors", out var errors)
                    && errors.ValueKind == JsonValueKind.Array)
                {
                    var errorStrings = errors.EnumerateArray()
                        .Where(error => error.ValueKind == JsonValueKind.String)
                        .Select(error => error.GetString());
                    return string.Join("; ", errorStrings);
                }

                return null;
            },
            cancellationToken);
    }
}
