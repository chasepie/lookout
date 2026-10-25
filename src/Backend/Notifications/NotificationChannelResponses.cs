using System.Text.Json;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>Reads the responses of the services Notification channels send to, such as Discord and Pushover.</summary>
internal static class NotificationChannelResponses
{
    /// <summary>
    /// Throws if the service refused the notification, saying why in its own words where it gave any. The message never
    /// includes the request's URL or body, which hold the Notification channel's secrets.
    /// </summary>
    public static async Task EnsureAcceptedAsync(
        HttpResponseMessage response,
        string service,
        Func<JsonElement, string?> reason,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = $"{service} returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})";
        try
        {
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
            if (body?.RootElement.ValueKind == JsonValueKind.Object
                && reason(body.RootElement) is { Length: > 0 } detail)
            {
                throw new HttpRequestException($"{message}: {detail}", null, response.StatusCode);
            }
        }
        catch (Exception error) when (error is not (HttpRequestException or OperationCanceledException))
        {
            // Proxies can answer with an HTML, empty or oddly encoded error; keep the HTTP status.
        }
        throw new HttpRequestException($"{message}.", null, response.StatusCode);
    }
}
