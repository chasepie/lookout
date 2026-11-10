using System.Globalization;
using System.Net.Http.Headers;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>
/// Where a Pushover notification goes, and how: a notification channel's settings with a subscription's options.
/// </summary>
/// <param name="ApplicationToken">The token of the Pushover application that sends.</param>
/// <param name="UserKey">The Pushover user or group key that receives.</param>
/// <param name="Priority">From -2, the quietest, to 2, an emergency that repeats until acknowledged.</param>
/// <param name="Sound">The name of a Pushover sound, or <see langword="null"/> for the user's default.</param>
/// <param name="RetrySeconds">How often an emergency notification repeats.</param>
/// <param name="ExpireSeconds">How long an emergency notification keeps repeating.</param>
public sealed record PushoverDestination(
    string ApplicationToken,
    string UserKey,
    int Priority,
    string? Sound,
    int RetrySeconds,
    int ExpireSeconds)
{
    /// <summary>
    /// Where a channel's notifications go, with how a subscription wants them sent.
    /// </summary>
    public static PushoverDestination For(PushoverChannelSettings channel, int priority, string? sound) => new(
        ApplicationToken: channel.ApplicationToken,
        UserKey: channel.UserKey,
        Priority: priority,
        Sound: sound,
        RetrySeconds: channel.RetrySeconds,
        ExpireSeconds: channel.ExpireSeconds);

    /// <summary>
    /// Leaves out the token and user key, so a destination that ends up in a log doesn't leak them.
    /// </summary>
    public override string ToString() =>
        $"{nameof(PushoverDestination)} {{ {nameof(Priority)} = {Number(Priority)}, {nameof(Sound)} = {Sound} }}";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Typed HTTP client for Pushover's message API, <c>https://pushover.net/api</c>.
/// </summary>
public class PushoverHttpClient(HttpClient httpClient)
{
    /// <summary>
    /// The priority that repeats until acknowledged, and so needs a retry and an expiry.
    /// </summary>
    public const int EmergencyPriority = 2;

    /// <summary>
    /// The largest attachment Pushover takes, 5 MB.
    /// </summary>
    public const int MaxAttachmentBytes = 5_242_880;

    private const int MaxTitleLength = 250;

    /// <summary>
    /// The longest message Pushover takes, in characters.
    /// </summary>
    public const int MaxMessageLength = 1024;

    private const string ScreenshotLeftOutNote = "\n\n(The screenshot was left out: it's over Pushover's 5 MB limit.)";

    public static void Configure(HttpClient httpClient)
    {
        httpClient.BaseAddress = new Uri("https://api.pushover.net/1/");
    }

    public async Task SendAsync(PushoverDestination destination, Notification notification, CancellationToken cancellationToken)
    {
        var screenshot = notification.Screenshot;
        var message = Cut(notification.Message, MaxMessageLength);
        if (screenshot is { Bytes.Length: > MaxAttachmentBytes })
        {
            screenshot = null;
            message = Cut(notification.Message, MaxMessageLength - ScreenshotLeftOutNote.Length) + ScreenshotLeftOutNote;
        }

        using var form = new MultipartFormDataContent
        {
            { new StringContent(destination.ApplicationToken), "token" },
            { new StringContent(destination.UserKey), "user" },
            { new StringContent(Cut(notification.Title, MaxTitleLength)), "title" },
            { new StringContent(message), "message" },
            { new StringContent(Number(destination.Priority)), "priority" },
        };
        if (destination.Priority == EmergencyPriority)
        {
            form.Add(new StringContent(Number(destination.RetrySeconds)), "retry");
            form.Add(new StringContent(Number(destination.ExpireSeconds)), "expire");
        }

        if (!string.IsNullOrWhiteSpace(destination.Sound))
        {
            form.Add(new StringContent(destination.Sound), "sound");
        }

        if (notification.Url is { } url)
        {
            form.Add(new StringContent(url.AbsoluteUri), "url");
        }

        if (screenshot is not null)
        {
            var image = new ByteArrayContent(screenshot.Bytes);
            image.Headers.ContentType = new MediaTypeHeaderValue(screenshot.MediaType);
            var extension = screenshot.MediaType == "image/jpeg" ? "jpg" : "png";
            form.Add(image, "attachment", $"screenshot.{extension}");
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsync("messages.json", form, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new NotificationDeliveryException($"Couldn't reach Pushover: {ex.Message}", retryable: true, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotificationDeliveryException("Couldn't reach Pushover: it timed out.", retryable: true, ex);
        }

        using (response)
        {
            await EnsureTakenAsync(response, cancellationToken);
        }
    }

    /// <exception cref="NotificationDeliveryException">Pushover didn't take the notification.</exception>
    private static async Task EnsureTakenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;

        // Pushover asks that 4xx not be retried, 429 included: it means the month's messages are used up
        if (status is >= 400 and < 500)
        {
            var errors = await ReadErrorsAsync(response, cancellationToken);
            throw new NotificationDeliveryException(
                $"Pushover refused the notification ({status})" + (errors is null ? "." : $": {errors}"), retryable: false);
        }

        throw new NotificationDeliveryException($"Pushover failed to take the notification ({status}).", retryable: true);
    }

    /// <summary>
    /// The errors Pushover gives in its response, joined, or <see langword="null"/> if it gives none.
    /// </summary>
    private static async Task<string?> ReadErrorsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<PushoverResponse>(cancellationToken);
            return body?.Errors is { Count: > 0 } errors ? string.Join("; ", errors) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not Pushover's JSON, such as a proxy's page or a body in a charset .NET doesn't know; the status still says it
            return null;
        }
    }

    private sealed record PushoverResponse(List<string>? Errors);

    /// <summary>
    /// Cuts <paramref name="text"/> to at most <paramref name="maxLength"/> UTF-16 code units, ending in "…" if it was
    /// cut, without splitting a character. Pushover counts characters, which are never more than code units.
    /// </summary>
    internal static string Cut(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var length = maxLength - 1;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length] + "…";
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
