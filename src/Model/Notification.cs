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

    public static Notification Change(string watchName, string oldContent, string newContent, string notificationUrl) =>
        new(NotificationKind.Change, $"{watchName} changed", $"{Shown(oldContent)} → {Shown(newContent)}", notificationUrl);

    public static Notification Errored(string watchName, string? errorMessage, string notificationUrl) =>
        new(NotificationKind.Errored, $"{watchName} errored",
            string.IsNullOrWhiteSpace(errorMessage) ? "Its Run ended without an error message." : errorMessage.Trim(),
            notificationUrl);

    public static Notification KnownFailures(string watchName, string? details, string notificationUrl) =>
        new(NotificationKind.KnownFailures, $"{watchName} failed {KnownFailuresBeforeNotifying} times in a row",
            string.IsNullOrWhiteSpace(details) ? "Its Script reported a Known failure." : details.Trim(), notificationUrl);

    public static Notification Recovered(string watchName, string content, string notificationUrl) =>
        new(NotificationKind.Recovered, $"{watchName} recovered", $"It succeeded again: {Shown(content)}", notificationUrl);

    public static Notification Test(string channelName) =>
        new(NotificationKind.Test, "Lookout test notification", $"{channelName} is set up to receive Lookout's notifications.");

    private static string Shown(string content) => content.Trim() is { Length: > 0 } trimmed ? trimmed : "(empty)";
}
