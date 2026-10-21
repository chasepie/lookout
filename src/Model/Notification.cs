namespace ChasePie.Lookout.Model;

public enum NotificationKind
{
    Change,
    Errored,
    /// <summary>The Watch's Script reported a Known failure <see cref="Notification.KnownFailuresBeforeNotifying"/> times in a row.</summary>
    KnownFailures,
    Recovered,
    Test
}

/// <summary>What a Channel is sent, worded once so every kind of Channel says the same thing.</summary>
public sealed record Notification(NotificationKind Kind, string Title, string Message, string? Url = null)
{
    /// <summary>A Known failure can be a passing block that a Rotation fixes, so only a third in a row notifies.</summary>
    public const int KnownFailuresBeforeNotifying = 3;

    /// <summary>
    /// How much of a Run's content a notification shows. A Change shows two contents, which together stay within the
    /// shortest message a Channel's service allows (Pushover's 1024 characters), so the new content is never cut off.
    /// </summary>
    private const int MaxShownContentLength = 480;

    public static Notification Change(string watchName, string oldContent, string newContent, string notificationUrl) => new(
        Kind: NotificationKind.Change,
        Title: $"{watchName} changed",
        Message: $"{Shown(oldContent)} → {Shown(newContent)}",
        Url: notificationUrl);

    public static Notification Errored(string watchName, string? errorMessage, string notificationUrl) => new(
        Kind: NotificationKind.Errored,
        Title: $"{watchName} errored",
        Message: string.IsNullOrWhiteSpace(errorMessage) ? "Its Run ended without an error message." : errorMessage.Trim(),
        Url: notificationUrl);

    public static Notification KnownFailures(string watchName, string? details, string notificationUrl) => new(
        Kind: NotificationKind.KnownFailures,
        Title: $"{watchName} failed {KnownFailuresBeforeNotifying} times in a row",
        Message: string.IsNullOrWhiteSpace(details) ? "Its Script reported a Known failure." : details.Trim(),
        Url: notificationUrl);

    public static Notification Recovered(string watchName, string content, string notificationUrl) => new(
        Kind: NotificationKind.Recovered,
        Title: $"{watchName} recovered",
        Message: $"It succeeded again: {Shown(content)}",
        Url: notificationUrl);

    public static Notification Test(string channelName) => new(
        Kind: NotificationKind.Test,
        Title: "Lookout test notification",
        Message: $"{channelName} is set up to receive Lookout's notifications.");

    /// <summary>Shortens text to at most <paramref name="maxLength"/> characters, ending it with an ellipsis if cut.</summary>
    public static string Fit(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }
        // A cut between the two halves of a surrogate pair, such as an emoji's, would leave half a character.
        var kept = char.IsHighSurrogate(text[maxLength - 2]) ? maxLength - 2 : maxLength - 1;
        return string.Concat(text.AsSpan(0, kept), "…");
    }

    private static string Shown(string content) =>
        content.Trim() is { Length: > 0 } trimmed ? Fit(trimmed, MaxShownContentLength) : "(empty)";
}
