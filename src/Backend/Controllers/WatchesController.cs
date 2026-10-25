using System.Diagnostics;
using System.Net;
using ChasePie.Lookout.Backend.Events;
using ChasePie.Lookout.Backend.Running;
using ChasePie.Lookout.Backend.Scheduling;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

[ApiController]
[Route("api/watches")]
public sealed class WatchesController(
    LookoutDbContext database,
    Runs runs,
    WatchSchedules schedules,
    WatchEvents watchEvents,
    TimeProvider timeProvider)
    : LookoutControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WatchSummary>>> List(CancellationToken cancellationToken)
    {
        var watches = await database.Watches.AsNoTracking()
            .OrderBy(watch => EF.Functions.Collate(watch.Name, "NOCASE"))
            .Select(WatchSummary.Projection)
            .ToListAsync(cancellationToken);
        return watches.ConvertAll(WithRunning);
    }

    [HttpPost]
    [RequestSizeLimit(128 * 1024)]
    public async Task<ActionResult<WatchSummary>> Create([FromForm] CreateWatchRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        var notificationUrl = request.NotificationUrl.Trim();
        if (ValidateSettings(name, notificationUrl, Watch.DefaultTimeoutSeconds) is { } invalid)
        {
            return invalid;
        }

        var (script, scriptProblem) = await UploadedScript.ReadAsync(request.Script, cancellationToken);
        if (script is null)
        {
            return Problem(scriptProblem!, statusCode: HttpStatusCode.BadRequest);
        }

        if (ScheduleOf(request) is not { } schedule)
        {
            return ScheduleMissing();
        }

        var watch = new Watch
        {
            Name = name,
            NotificationUrl = notificationUrl,
            Schedule = schedule,
            UsesVpn = request.UsesVpn,
            JitterMinutes = request.JitterMinutes
        };
        if (schedules.ScheduleFirstRun(watch) is { } scheduleProblem)
        {
            return Problem(scheduleProblem, statusCode: HttpStatusCode.BadRequest);
        }

        if (await FindChannels(request.ChannelIds, cancellationToken) is not { } channels)
        {
            return ChannelMissing();
        }
        watch.Channels = channels;
        var version = new ScriptVersion
        {
            WatchId = watch.Id,
            Number = 1,
            UploadedAt = timeProvider.GetUtcNow(),
            Source = script.Source,
            FileKind = script.FileKind
        };
        watch.ScriptVersions.Add(version);

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        database.Watches.Add(watch);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (channels.Count > 0)
        {
            // A chosen Channel was deleted since it was read, failing its join row's foreign key.
            return Problem("One of the chosen Channels was deleted while the Watch was being created.", statusCode: HttpStatusCode.Conflict);
        }

        watch.CurrentScriptVersionId = version.Id;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return StatusCode(StatusCodes.Status201Created, WatchSummary.From(watch));
    }

    [HttpPut("{id:guid}/schedule")]
    public async Task<ActionResult<WatchSummary>> ChangeSchedule(Guid id, ScheduleRequest request, CancellationToken cancellationToken)
    {
        if (ScheduleOf(request) is not { } schedule)
        {
            return ScheduleMissing();
        }

        var update = await schedules.ChangeScheduleAsync(id, schedule, request.JitterMinutes, cancellationToken);
        return update.Status switch
        {
            // Read back, unless the Watch was deleted since.
            ScheduleUpdateStatus.Changed => await Get(id, cancellationToken),
            ScheduleUpdateStatus.NotFound => NotFound(),
            ScheduleUpdateStatus.Invalid => Problem(update.Problem!, statusCode: HttpStatusCode.BadRequest),
            ScheduleUpdateStatus.KeptChanging => Problem("Runs of this Watch kept starting while its schedule was being saved. Please try again.", statusCode: HttpStatusCode.Conflict),
            _ => throw new UnreachableException()
        };
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WatchSummary>> Get(Guid id, CancellationToken cancellationToken)
    {
        var watch = await database.Watches.AsNoTracking()
            .Where(watch => watch.Id == id)
            .Select(WatchSummary.Projection)
            .SingleOrDefaultAsync(cancellationToken);
        return watch is null ? NotFound() : WithRunning(watch);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Edit(Guid id, EditWatchRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        var notificationUrl = request.NotificationUrl.Trim();
        if (ValidateSettings(name, notificationUrl, request.TimeoutSeconds) is { } invalid)
        {
            return invalid;
        }

        // A set-based update leaves the Revision alone, so it neither races nor supersedes a Run being recorded.
        var updated = await database.Watches
            .Where(watch => watch.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(watch => watch.Name, name)
                .SetProperty(watch => watch.NotificationUrl, notificationUrl)
                .SetProperty(watch => watch.TimeoutSeconds, request.TimeoutSeconds)
                .SetProperty(watch => watch.UsesVpn, request.UsesVpn), cancellationToken);
        return updated == 0 ? NotFound() : NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        // A set-based delete leaves the cascades to the Watch's Script versions and Runs to SQLite.
        var deleted = await database.Watches
            .Where(watch => watch.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? NotFound() : NoContent();
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<IActionResult> Pause(Guid id, CancellationToken cancellationToken)
    {
        switch ((await schedules.PauseAsync(id, cancellationToken)).Status)
        {
            case ScheduleUpdateStatus.Changed:
                await watchEvents.StatusChangedAsync(id, WatchStatus.Paused);
                return NoContent();
            case ScheduleUpdateStatus.Unchanged:
                return NoContent();
            case ScheduleUpdateStatus.NotFound:
                return NotFound();
            default:
                throw new UnreachableException();
        }
    }

    [HttpPost("{id:guid}/resume")]
    public async Task<IActionResult> Resume(Guid id, CancellationToken cancellationToken)
    {
        switch ((await schedules.ResumeAsync(id, cancellationToken)).Status)
        {
            case ScheduleUpdateStatus.Changed:
                if (await WatchStatuses.ReadAsync(database.Watches, id, CancellationToken.None) is { } resumed)
                {
                    await watchEvents.StatusChangedAsync(id, resumed);
                }
                return NoContent();
            case ScheduleUpdateStatus.Unchanged:
                return NoContent();
            case ScheduleUpdateStatus.NotFound:
                return NotFound();
            case ScheduleUpdateStatus.KeptChanging:
                return Problem("The schedule of this Watch kept changing while it was being resumed. Please try again.", statusCode: HttpStatusCode.Conflict);
            default:
                throw new UnreachableException();
        }
    }

    [HttpPut("{id:guid}/channels")]
    public async Task<IActionResult> ChangeChannels(Guid id, ChannelsRequest request, CancellationToken cancellationToken)
    {
        if (!await database.Watches.AnyAsync(watch => watch.Id == id, cancellationToken))
        {
            return NotFound();
        }

        if (await FindChannels(request.ChannelIds, cancellationToken) is not { } channels)
        {
            return ChannelMissing();
        }

        // The choice is replaced whole in a transaction that writes before anything else, so SQLite runs overlapping
        // changes one after another and the last one wins, rather than combining them. Only the choice changes, so the
        // Watch's Revision is left alone and a Run in progress is still recorded.
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await database.WatchChannels
                .Where(choice => choice.WatchId == id)
                .ExecuteDeleteAsync(cancellationToken);
            database.WatchChannels.AddRange(channels.Select(channel => new WatchChannel { WatchId = id, ChannelId = channel.Id }));
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The Watch or a chosen Channel was deleted since they were read.
            return Problem("The Watch or one of the chosen Channels no longer exists.", statusCode: HttpStatusCode.Conflict);
        }
        return NoContent();
    }

    /// <summary>The chosen Channels, or null if any of them doesn't exist.</summary>
    private async Task<List<Channel>?> FindChannels(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var chosen = ids.Distinct().ToList();
        if (chosen.Count == 0)
        {
            return [];
        }
        var channels = await database.Channels
            .Where(channel => chosen.Contains(channel.Id))
            .ToListAsync(cancellationToken);
        return channels.Count == chosen.Count ? channels : null;
    }

    private ObjectResult ChannelMissing() =>
        Problem("One of the chosen Channels no longer exists.", statusCode: HttpStatusCode.BadRequest);

    private WatchSummary WithRunning(WatchSummary watch) => watch with { IsRunning = runs.IsRunning(watch.Id) };

    private ObjectResult? ValidateSettings(string name, string notificationUrl, int timeoutSeconds)
    {
        if (name.Length is < 1 or > 200)
        {
            return Problem("Name must contain between 1 and 200 characters.", statusCode: HttpStatusCode.BadRequest);
        }

        if (!HttpUrls.IsValid(notificationUrl))
        {
            return Problem("Notification URL must be an HTTP or HTTPS URL.", statusCode: HttpStatusCode.BadRequest);
        }

        if (timeoutSeconds is < 1 or > Watch.MaxTimeoutSeconds)
        {
            return Problem($"Timeout must be between 1 and {Watch.MaxTimeoutSeconds} seconds.", statusCode: HttpStatusCode.BadRequest);
        }

        return null;
    }

    /// <summary>The requested Schedule, which <see cref="WatchSchedules"/> checks, or null if no kind was chosen.</summary>
    private static Schedule? ScheduleOf(ScheduleRequest request) => request.ScheduleKind switch
    {
        ScheduleKind.Interval => new Schedule { Kind = ScheduleKind.Interval, Every = request.Every, Unit = request.Unit },
        ScheduleKind.Daily when request.TimeOfDay is { } timeOfDay => Schedule.DailyAt(timeOfDay),
        ScheduleKind.Daily => new Schedule { Kind = ScheduleKind.Daily },
        ScheduleKind.Cron => Schedule.FromCron(request.Cron ?? ""),
        _ => null
    };

    private ObjectResult ScheduleMissing() =>
        Problem("Choose an interval, daily or cron schedule.", statusCode: HttpStatusCode.BadRequest);
}
