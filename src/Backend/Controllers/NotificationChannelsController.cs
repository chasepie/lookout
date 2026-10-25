using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

/// <summary>The Notification channels Watches can notify. Responses never include a Notification channel's secrets.</summary>
[ApiController]
[Route("api/notification-channels")]
public sealed partial class NotificationChannelsController(LookoutDbContext database, NotificationDispatcher notifications)
    : LookoutControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<NotificationChannelSummary>> List(CancellationToken cancellationToken)
    {
        return await database.NotificationChannels.AsNoTracking()
            .OrderBy(notificationChannel => EF.Functions.Collate(notificationChannel.Name, "NOCASE"))
            .Select(NotificationChannelSummary.Projection)
            .ToListAsync(cancellationToken);
    }

    [HttpPost]
    public async Task<ActionResult<NotificationChannelSummary>> Create(NotificationChannelRequest request, CancellationToken cancellationToken)
    {
        if (!TryBuildNotificationChannel(request, out var notificationChannel, out var problem))
        {
            return problem;
        }

        database.NotificationChannels.Add(notificationChannel);
        await database.SaveChangesAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, NotificationChannelSummary.From(notificationChannel));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        // SQLite cascades the delete to every Watch's choice of this Notification channel.
        var deleted = await database.NotificationChannels
            .Where(notificationChannel => notificationChannel.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? NotFound() : NoContent();
    }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> SendTest(Guid id, CancellationToken cancellationToken)
    {
        var notificationChannel = await database.NotificationChannels.AsNoTracking()
            .SingleOrDefaultAsync(nc => nc.Id == id, cancellationToken);
        if (notificationChannel is null)
        {
            return NotFound();
        }

        try
        {
            await notifications.SendAsync(notificationChannel, Notification.Test(notificationChannel.Name), cancellationToken);
        }
        // The Notification channel's service refused or didn't answer in time; the request itself wasn't abandoned.
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return Problem(
                detail: $"Could not send the test notification: {error.Message}",
                statusCode: HttpStatusCode.BadGateway);
        }
        return NoContent();
    }

    private bool TryBuildNotificationChannel(
        NotificationChannelRequest request,
        [NotNullWhen(true)] out NotificationChannel? nc,
        [NotNullWhen(false)] out ObjectResult? problem)
    {
        nc = null;
        problem = null;

        var name = request.Name?.Trim() ?? "";
        string? message = null;
        if (name.Length is < 1 or > 100)
        {
            message = "Name must contain between 1 and 100 characters.";
        }
        else if (request.Kind == NotificationChannelKind.Discord)
        {
            var webhookUrl = request.WebhookUrl?.Trim() ?? "";
            if (!HttpUrls.IsValid(webhookUrl))
            {
                message = "Webhook URL must be an HTTP or HTTPS URL, such as https://discord.com/api/webhooks/….";
            }
            else
            {
                nc = new NotificationChannel
                {
                    Name = name,
                    Kind = NotificationChannelKind.Discord,
                    WebhookUrl = webhookUrl
                };
            }
        }
        else if (request.Kind == NotificationChannelKind.Pushover)
        {
            var userKey = request.UserKey?.Trim() ?? "";
            var appToken = request.AppToken?.Trim() ?? "";
            if (!PushoverKey().IsMatch(userKey) || !PushoverKey().IsMatch(appToken))
            {
                message = "Pushover's user key and app token are each 30 letters and digits.";
            }
            else
            {
                nc = new NotificationChannel
                {
                    Name = name,
                    Kind = NotificationChannelKind.Pushover,
                    UserKey = userKey,
                    AppToken = appToken
                };
            }
        }
        else
        {
            message = "Choose a Discord or Pushover Notification channel.";
        }

        if (message is not null)
        {
            problem = Problem(detail: message, statusCode: HttpStatusCode.BadRequest);
        }

        return nc is not null;
    }

    [GeneratedRegex("^[A-Za-z0-9]{30}$")]
    private static partial Regex PushoverKey();
}
