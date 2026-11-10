namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>
/// A message about something a run showed, before it's fitted to a notification channel's service.
/// </summary>
/// <param name="Title">The watch's name.</param>
/// <param name="Message">What the run showed.</param>
/// <param name="Url">A link to the run, when Lookout knows its public address.</param>
/// <param name="Screenshot">The run's screenshot, when it has one.</param>
public sealed record Notification(
    string Title,
    string Message,
    Uri? Url = null,
    NotificationScreenshot? Screenshot = null);

/// <param name="MediaType"><c>image/png</c> or <c>image/jpeg</c>.</param>
public sealed record NotificationScreenshot(byte[] Bytes, string MediaType);
