using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

/// <summary>
/// Which of a watch's events go to which notification channels, and how.
/// </summary>
[ApiController]
[Route("api/Watches/{watchId:int}/subscriptions")]
public class SubscriptionsController(LookoutDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<SubscriptionDto>>> GetAll(int watchId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Watches.AnyAsync(w => w.Id == watchId, cancellationToken))
        {
            return NotFound();
        }

        var subscriptions = await dbContext.Subscriptions
            .AsNoTracking()
            .Where(s => s.WatchId == watchId)
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken);
        return subscriptions.ConvertAll(ToDto);
    }

    /// <summary>
    /// Replaces all of the watch's subscriptions.
    /// </summary>
    [HttpPut]
    public async Task<ActionResult<List<SubscriptionDto>>> ReplaceAll(
        int watchId, List<SubscriptionDto> subscriptions, CancellationToken cancellationToken)
    {
        if (!await dbContext.Watches.AnyAsync(w => w.Id == watchId, cancellationToken))
        {
            return NotFound();
        }

        var channelIds = subscriptions
            .Select(s => s.NotificationChannelId)
            .Distinct()
            .ToList();
        var channels = await dbContext.NotificationChannels
            .AsNoTracking()
            .Where(c => channelIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);
        if (Problem(subscriptions, channels) is { } problem)
        {
            return BadRequest(problem);
        }

        // One transaction, so a failed save, such as for a channel deleted meanwhile, keeps the subscriptions there were
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Subscriptions
            .Where(s => s.WatchId == watchId)
            .ExecuteDeleteAsync(cancellationToken);

        dbContext.Subscriptions.AddRange(subscriptions.Select(dto => new Subscription
        {
            WatchId = watchId,
            NotificationChannelId = dto.NotificationChannelId,
            Events = [.. dto.Events.Distinct()],
            Pushover = dto.Pushover is { } pushover
                ? new PushoverSubscriptionOptions
                {
                    Priority = pushover.Priority,
                    Sound = string.IsNullOrWhiteSpace(pushover.Sound)
                        ? null
                        : pushover.Sound.Trim(),
                }
                : null,
        }));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAll(watchId, cancellationToken);
    }

    /// <summary>
    /// What's wrong with the subscriptions, or <see langword="null"/> if nothing is.
    /// </summary>
    private static string? Problem(List<SubscriptionDto> subscriptions, Dictionary<int, NotificationChannel> channels)
    {
        var sent = new HashSet<(int ChannelId, WatchEvent Event)>();
        foreach (var subscription in subscriptions)
        {
            if (!channels.TryGetValue(subscription.NotificationChannelId, out var channel))
            {
                return $"There's no notification channel {subscription.NotificationChannelId}.";
            }

            if (subscription.Events.Count == 0)
            {
                return $"A subscription to {channel.Name} must send at least one event.";
            }

            if (channel.Service == NotificationService.Pushover && subscription.Pushover is null)
            {
                return $"A subscription to {channel.Name} needs its Pushover options.";
            }

            foreach (var watchEvent in subscription.Events.Distinct())
            {
                // Enums come in as numbers, which may name no event
                if (!Enum.IsDefined(watchEvent))
                {
                    return $"There's no event {watchEvent}.";
                }

                if (!sent.Add((channel.Id, watchEvent)))
                {
                    return $"{watchEvent} is sent to {channel.Name} more than once.";
                }
            }
        }

        return null;
    }

    private static SubscriptionDto ToDto(Subscription subscription) => new()
    {
        NotificationChannelId = subscription.NotificationChannelId,
        Events = subscription.Events,
        Pushover = subscription.Pushover is { } pushover
            ? new PushoverSubscriptionDto { Priority = pushover.Priority, Sound = pushover.Sound }
            : null,
    };
}
