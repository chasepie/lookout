using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

/// <summary>
/// The notification channels the Owner has set up. Credentials go in but never come out.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class NotificationChannelsController(LookoutDbContext dbContext, NotificationSender sender) : ControllerBase
{
    private static readonly Notification TestNotification = new(
        Title: "Lookout", Message: "This is a test notification from Lookout.");

    [HttpGet]
    public async Task<ActionResult<List<NotificationChannelSummary>>> GetAll(CancellationToken cancellationToken) =>
        await ListAsync(dbContext.NotificationChannels, cancellationToken);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<NotificationChannelSummary>> Get(int id, CancellationToken cancellationToken)
    {
        var channels = await ListAsync(dbContext.NotificationChannels.Where(c => c.Id == id), cancellationToken);
        return channels.Count == 0 ? NotFound() : channels[0];
    }

    [HttpPost]
    public async Task<ActionResult<NotificationChannelSummary>> Create(
        NotificationChannelDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Pushover?.ApplicationToken) || string.IsNullOrWhiteSpace(dto.Pushover.UserKey))
        {
            return BadRequest("A Pushover channel needs its application token and user key.");
        }

        var channel = new NotificationChannel
        {
            Name = dto.Name.Trim(),
            Service = dto.Service,
            Pushover = new PushoverChannelSettings
            {
                ApplicationToken = dto.Pushover.ApplicationToken.Trim(),
                UserKey = dto.Pushover.UserKey.Trim(),
                RetrySeconds = dto.Pushover.RetrySeconds,
                ExpireSeconds = dto.Pushover.ExpireSeconds,
            },
        };
        dbContext.NotificationChannels.Add(channel);
        await dbContext.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = channel.Id }, Summarize(channel, []));
    }

    /// <summary>
    /// Edits a channel. A blank or missing token or user key keeps the one it has.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<NotificationChannelSummary>> Update(
        int id, NotificationChannelDto dto, CancellationToken cancellationToken)
    {
        var channel = await dbContext.NotificationChannels.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        if (dto.Service != channel.Service
            || channel.Pushover is not { } settings
            || dto.Pushover is not { } pushoverDto)
        {
            return BadRequest("A channel's service can't be changed.");
        }

        channel.Name = dto.Name.Trim();
        if (!string.IsNullOrWhiteSpace(pushoverDto.ApplicationToken))
        {
            settings.ApplicationToken = pushoverDto.ApplicationToken.Trim();
        }

        if (!string.IsNullOrWhiteSpace(pushoverDto.UserKey))
        {
            settings.UserKey = pushoverDto.UserKey.Trim();
        }

        settings.RetrySeconds = pushoverDto.RetrySeconds;
        settings.ExpireSeconds = pushoverDto.ExpireSeconds;
        await dbContext.SaveChangesAsync(cancellationToken);
        return await Get(id, cancellationToken);
    }

    /// <summary>
    /// Deletes a channel, and the subscriptions that send to it.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        // The database deletes the channel's subscriptions with it
        var deleted = await dbContext.NotificationChannels
            .Where(c => c.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? NotFound() : NoContent();
    }

    /// <summary>
    /// Sends a test notification through the channel right away, at normal priority, without retrying.
    /// </summary>
    /// <response code="502">The channel's service didn't take it; the problem's detail says why.</response>
    [HttpPost("{id:int}/test")]
    public async Task<IActionResult> SendTest(int id, CancellationToken cancellationToken)
    {
        var channel = await dbContext.NotificationChannels
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel?.Pushover is not { } settings)
        {
            return NotFound();
        }

        try
        {
            await sender.SendNowAsync(
                destination: PushoverDestination.For(settings, priority: 0, sound: null),
                notification: TestNotification,
                cancellationToken);
        }
        catch (NotificationDeliveryException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        return NoContent();
    }

    /// <summary>
    /// Reads channels in the order they were set up, each with the watches that use it.
    /// </summary>
    private static async Task<List<NotificationChannelSummary>> ListAsync(
        IQueryable<NotificationChannel> channels, CancellationToken cancellationToken)
    {
        var rows = await channels
            .AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new
            {
                Channel = c,
                // One per subscription; SQLite can't make them distinct in this query
                Watches = c.Subscriptions
                    .Select(s => new WatchReference(s.Watch.Id, s.Watch.Name))
                    .ToList(),
            })
            .ToListAsync(cancellationToken);
        return rows.ConvertAll(row => Summarize(row.Channel, [.. row.Watches.Distinct()]));
    }

    private static NotificationChannelSummary Summarize(NotificationChannel channel, List<WatchReference> watches) => new(
        Id: channel.Id,
        Name: channel.Name,
        Service: channel.Service,
        Pushover: channel.Pushover is { } pushover
            ? new PushoverChannelSummary(pushover.RetrySeconds, pushover.ExpireSeconds)
            : null,
        Watches: [.. watches.OrderBy(w => w.Name, WatchesController.NameOrder).ThenBy(w => w.Id)]);
}
