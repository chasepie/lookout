using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>
/// Sends the notifications a finished run calls for: its event, if it shows one, to each of its watch's subscriptions
/// that sends that event.
/// </summary>
public sealed class RunNotifications(LookoutDbContext dbContext, NotificationSender sender, RunLinks runLinks)
{
    /// <param name="run">A finished run, saved, with its watch.</param>
    public async Task NotifyAsync(WatchRun run, CancellationToken cancellationToken)
    {
        var subscriptions = await dbContext.Subscriptions
            .AsNoTracking()
            .Include(s => s.NotificationChannel)
            .Where(s => s.WatchId == run.WatchId)
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken);
        if (subscriptions.Count == 0)
        {
            return;
        }

        // Without their screenshots, which only the run itself needs
        var earlierRuns = await dbContext.WatchRuns
            .AsNoTracking()
            .Where(r => r.WatchId == run.WatchId && r.Id < run.Id && r.Status == WatchRunStatus.Done)
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
            })
            .ToListAsync(cancellationToken);
        var threshold = run.Watch.KnownFailureThreshold;
        if (WatchEvents.Detect(run, earlierRuns, threshold) is not { } watchEvent)
        {
            return;
        }

        var screenshot = run is { Screenshot: { } bytes, ScreenshotMediaType: { } mediaType }
            ? new NotificationScreenshot(bytes, mediaType)
            : null;
        var notification = new Notification(
            Title: run.Watch.Name,
            Message: Describe(watchEvent, run, earlierRuns, threshold),
            Url: runLinks.For(run),
            Screenshot: screenshot);
        foreach (var subscription in subscriptions.Where(s => s.Events.Contains(watchEvent)))
        {
            if (subscription.NotificationChannel.Pushover is { } channel && subscription.Pushover is { } options)
            {
                sender.Enqueue(new NotificationDelivery(
                    subscription.NotificationChannel.Name,
                    PushoverDestination.For(channel, options.Priority, options.Sound),
                    notification));
            }
        }
    }

    /// <summary>
    /// What a notification of <paramref name="watchEvent"/> says about <paramref name="run"/>.
    /// </summary>
    private static string Describe(
        WatchEvent watchEvent, WatchRun run, IReadOnlyList<WatchRun> earlierRunsNewestFirst, int threshold) =>
        watchEvent switch
        {
            WatchEvent.ContentChange =>
                $"{earlierRunsNewestFirst.First(r => r.Content is not null && r.Error is null).Content} → {run.Content}",
            WatchEvent.KnownFailureStreak => $"Known failure {threshold} times in a row: {run.FailureReason}",
            WatchEvent.Crash => FirstLine(run.Error!),
            WatchEvent.Recovery => $"Back to normal: {run.Content}",
            _ => throw new ArgumentOutOfRangeException(nameof(watchEvent), watchEvent, null),
        };

    private static string FirstLine(string text) => text.Split('\n', 2)[0].TrimEnd('\r');
}
