using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>
/// The event a finished run showed, with the earlier runs it was worked out from, which say what changed.
/// </summary>
public sealed record RunEvent(WatchEvent Event, IReadOnlyList<WatchRun> EarlierRunsNewestFirst);

/// <summary>
/// Works out the event a finished run shows, whether or not anything is subscribed to it, then sends it to each of its
/// watch's subscriptions that sends that event.
/// </summary>
public sealed class RunNotifications(LookoutDbContext dbContext, NotificationSender sender, RunLinks runLinks)
{
    /// <summary>
    /// The event <paramref name="run"/> shows, worked out before it's saved, so it's kept with the run.
    /// </summary>
    /// <param name="run">A run that has ended but isn't yet saved as done, with its watch.</param>
    /// <returns>The event, or <see langword="null"/> if it shows none.</returns>
    public async Task<RunEvent?> DetectAsync(WatchRun run, CancellationToken cancellationToken)
    {
        var earlierRunsQuery = dbContext.WatchRuns
            .AsNoTracking()
            .Where(r => r.WatchId == run.WatchId
                && r.Id < run.Id
                && r.Status == WatchRunStatus.Done);

        // Only the runs since the watch's previous success, and that success, bear on the run's event, so the rest of its
        // history isn't read on every run
        var previousSuccessId = await earlierRunsQuery
            .Where(WatchEvents.IsSuccessQuery)
            .MaxAsync(r => (int?)r.Id, cancellationToken) ?? 0;

        // Without their screenshots, which only the run itself needs
        var earlierRuns = await earlierRunsQuery
            .Where(r => r.Id >= previousSuccessId)
            .OrderByDescending(r => r.Id)
            .Select(r => new WatchRun
            {
                Id = r.Id,
                WatchId = r.WatchId,
                CreatedTime = r.CreatedTime,
                Status = r.Status,
                Content = r.Content,
                FailureReason = r.FailureReason,
                Error = r.Error,
                Interrupted = r.Interrupted,
                Event = r.Event,
            })
            .ToListAsync(cancellationToken);
        return WatchEvents.Detect(run, earlierRuns, run.Watch.KnownFailureThreshold) is { } watchEvent
            ? new RunEvent(watchEvent, earlierRuns)
            : null;
    }

    /// <summary>
    /// Queues a notification of <paramref name="runEvent"/> for each of the watch's subscriptions that sends it.
    /// </summary>
    /// <param name="run">The run, saved, with its watch.</param>
    public async Task NotifyAsync(WatchRun run, RunEvent runEvent, CancellationToken cancellationToken)
    {
        var watchEvent = runEvent.Event;
        var subscriptions = await dbContext.Subscriptions
            .AsNoTracking()
            .Include(s => s.NotificationChannel)
            .Where(s => s.WatchId == run.WatchId)
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken);
        subscriptions.RemoveAll(s => !s.Events.Contains(watchEvent));
        if (subscriptions.Count == 0)
        {
            return;
        }

        var screenshot = run is { Screenshot: { } bytes, ScreenshotMediaType: { } mediaType }
            ? new NotificationScreenshot(bytes, mediaType)
            : null;
        var notification = new Notification(
            Title: run.Watch.Name,
            Message: Describe(watchEvent, run, runEvent.EarlierRunsNewestFirst),
            Url: runLinks.For(run),
            Screenshot: screenshot);
        foreach (var subscription in subscriptions)
        {
            if (subscription.NotificationChannel.Pushover is { } channel
                && subscription.Pushover is { } options)
            {
                sender.Enqueue(new NotificationDelivery(
                    ChannelName: subscription.NotificationChannel.Name,
                    Destination: PushoverDestination.For(channel, options.Priority, options.Sound),
                    Notification: notification));
            }
        }
    }

    /// <summary>
    /// What a notification of <paramref name="watchEvent"/> says about <paramref name="run"/>.
    /// </summary>
    private static string Describe(
        WatchEvent watchEvent, WatchRun run, IReadOnlyList<WatchRun> earlierRunsNewestFirst) =>
        watchEvent switch
        {
            WatchEvent.ContentChange =>
                DescribeContentChange(earlierRunsNewestFirst.First(WatchEvents.IsSuccess).Content!, run.Content!),
            WatchEvent.KnownFailureStreak =>
                $"Known failure {WatchEvents.KnownFailureStreakLength(earlierRunsNewestFirst)} times in a row: {run.FailureReason}",
            WatchEvent.Crash => FirstLine(run.Error!),
            WatchEvent.Recovery => $"Recovered: {run.Content}",
            _ => throw new ArgumentOutOfRangeException(nameof(watchEvent), watchEvent, null),
        };

    /// <summary>
    /// "Before → after", with a long "before" cut short so that "after", the news, still fits in a notification.
    /// </summary>
    private static string DescribeContentChange(string before, string after)
    {
        const string Arrow = " → ";
        var room = Math.Max(
            PushoverHttpClient.MaxMessageLength / 4,
            PushoverHttpClient.MaxMessageLength - Arrow.Length - after.Length);
        return PushoverHttpClient.Cut(before, room) + Arrow + after;
    }

    /// <summary>
    /// The first line with something on it, since a notification's message can't be blank.
    /// </summary>
    private static string FirstLine(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r')).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
        ?? "The run crashed without saying why.";
}
