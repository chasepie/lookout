using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Net;
using System.Text.RegularExpressions;
using ChasePie.Lookout.Backend.Notifications;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

/// <summary>The Channels Watches can notify. Responses never include a Channel's secrets.</summary>
[ApiController]
[Route("api/channels")]
public sealed partial class ChannelsController(LookoutDbContext database, NotificationDispatcher notifications)
    : LookoutControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<ChannelSummary>> List(CancellationToken cancellationToken)
    {
        return await database.Channels.AsNoTracking()
            .OrderBy(channel => EF.Functions.Collate(channel.Name, "NOCASE"))
            .Select(ChannelSummary.Projection)
            .ToListAsync(cancellationToken);
    }

    [HttpPost]
    public async Task<ActionResult<ChannelSummary>> Create(ChannelRequest request, CancellationToken cancellationToken)
    {
        if (!TryBuildChannel(request, out var channel, out var problem))
        {
            return problem;
        }

        database.Channels.Add(channel);
        await database.SaveChangesAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ChannelSummary.From(channel));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        // SQLite cascades the delete to every Watch's choice of this Channel.
        var deleted = await database.Channels
            .Where(channel => channel.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? NotFound() : NoContent();
    }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> SendTest(Guid id, CancellationToken cancellationToken)
    {
        var channel = await database.Channels.AsNoTracking().SingleOrDefaultAsync(channel => channel.Id == id, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        try
        {
            await notifications.SendAsync(channel, Notification.Test(channel.Name), cancellationToken);
        }
        // The Channel's service refused or didn't answer in time; the request itself wasn't abandoned.
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return Problem($"Could not send the test notification: {error.Message}", statusCode: HttpStatusCode.BadGateway);
        }
        return NoContent();
    }

    private bool TryBuildChannel(
        ChannelRequest request,
        [NotNullWhen(true)] out Channel? channel,
        [NotNullWhen(false)] out ObjectResult? problem)
    {
        channel = null;
        var name = request.Name?.Trim() ?? "";
        string? message = null;
        if (name.Length is < 1 or > 100)
        {
            message = "Name must contain between 1 and 100 characters.";
        }
        else if (request.Kind == ChannelKind.Discord)
        {
            var webhookUrl = request.WebhookUrl?.Trim() ?? "";
            if (!HttpUrls.IsValid(webhookUrl))
            {
                message = "Webhook URL must be an HTTP or HTTPS URL, such as https://discord.com/api/webhooks/….";
            }
            else
            {
                channel = new Channel { Name = name, Kind = ChannelKind.Discord, WebhookUrl = webhookUrl };
            }
        }
        else if (request.Kind == ChannelKind.Pushover)
        {
            var userKey = request.UserKey?.Trim() ?? "";
            var appToken = request.AppToken?.Trim() ?? "";
            if (!PushoverKey().IsMatch(userKey) || !PushoverKey().IsMatch(appToken))
            {
                message = "Pushover's user key and app token are each 30 letters and digits.";
            }
            else
            {
                channel = new Channel { Name = name, Kind = ChannelKind.Pushover, UserKey = userKey, AppToken = appToken };
            }
        }
        else
        {
            message = "Choose a Discord or Pushover Channel.";
        }

        problem = message is null ? null : Problem(message, statusCode: HttpStatusCode.BadRequest);
        return channel is not null;
    }

    [GeneratedRegex("^[A-Za-z0-9]{30}$")]
    private static partial Regex PushoverKey();
}

public sealed record ChannelSummary(Guid Id, string Name, ChannelKind Kind)
{
    /// <summary>Maps a Channel in a database query, leaving its secrets behind; <see cref="From"/> does the same in memory.</summary>
    public static readonly Expression<Func<Channel, ChannelSummary>> Projection =
        channel => new ChannelSummary(channel.Id, channel.Name, channel.Kind);

    private static readonly Func<Channel, ChannelSummary> CompiledProjection = Projection.Compile();

    public static ChannelSummary From(Channel channel) => CompiledProjection(channel);
}

public sealed class ChannelRequest
{
    public string? Name { get; init; }
    public ChannelKind? Kind { get; init; }
    public string? WebhookUrl { get; init; }
    public string? UserKey { get; init; }
    public string? AppToken { get; init; }
}
