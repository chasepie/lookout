using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Backend.Scheduling;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace ChasePie.Lookout.Backend.Controllers;

[ApiController]
[Route("api/watches")]
public sealed class WatchesController(
    LookoutDbContext database,
    RunExecutor runExecutor,
    WatchSchedules schedules,
    RunningWatches runningWatches,
    RunSlots runSlots,
    IHostApplicationLifetime applicationLifetime)
    : LookoutControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WatchSummary>>> List(CancellationToken cancellationToken)
    {
        return await database.Watches.AsNoTracking()
            .OrderBy(watch => EF.Functions.Collate(watch.Name, "NOCASE"))
            .Select(WatchSummary.Projection)
            .ToListAsync(cancellationToken);
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

        var fileKind = Path.GetExtension(request.Script.FileName).ToLowerInvariant();
        if (fileKind is not (".ts" or ".js") || request.Script.Length is < 1 or > 65536)
        {
            return Problem("Upload a .ts or .js Script between 1 byte and 64 KiB.", statusCode: HttpStatusCode.BadRequest);
        }

        if (!TryBuildSchedule(request, out var schedule, out var scheduleProblem))
        {
            return scheduleProblem;
        }

        using var reader = new StreamReader(request.Script.OpenReadStream());
        var source = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(source))
        {
            return Problem("Script source must not be empty.", statusCode: HttpStatusCode.BadRequest);
        }

        if (await FindChannels(request.ChannelIds, cancellationToken) is not { } channels)
        {
            return ChannelMissing();
        }

        var watch = new Watch
        {
            Name = name,
            NotificationUrl = notificationUrl,
            Schedule = schedule,
            JitterMinutes = request.JitterMinutes,
            NextRunAt = schedules.NextRunAt(schedule, request.JitterMinutes),
            NextRunTimeZone = schedules.TimeZone.Id,
            Channels = channels
        };
        var version = new ScriptVersion
        {
            WatchId = watch.Id,
            Source = source,
            FileKind = fileKind[1..]
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
        if (!TryBuildSchedule(request, out var schedule, out var scheduleProblem))
        {
            return scheduleProblem;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await database.Watches
                .Where(watch => watch.Id == id)
                .Select(watch => new { watch.LastScheduledRunAt })
                .SingleOrDefaultAsync(cancellationToken);
            if (current is null)
            {
                return NotFound();
            }

            // A set-based update leaves Revision alone, so a Run finishing meanwhile is still recorded. It applies
            // only if the scheduler started no Run since the read, as the next Run must stay clear of the last one.
            var nextRunAt = schedules.NextRunAt(schedule, request.JitterMinutes, current.LastScheduledRunAt);
            var updated = await database.Watches
                .Where(watch => watch.Id == id && watch.LastScheduledRunAt == current.LastScheduledRunAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(watch => watch.Schedule, schedule)
                    .SetProperty(watch => watch.JitterMinutes, request.JitterMinutes)
                    // A paused Watch has no next Run until it is resumed.
                    .SetProperty(watch => watch.NextRunAt, watch => watch.IsPaused ? null : nextRunAt)
                    .SetProperty(watch => watch.NextRunTimeZone, schedules.TimeZone.Id), cancellationToken);
            if (updated == 1)
            {
                return await database.Watches.AsNoTracking()
                    .Where(watch => watch.Id == id)
                    .Select(WatchSummary.Projection)
                    .SingleAsync(cancellationToken);
            }
        }
        return Problem("Runs of this Watch kept starting while its schedule was being saved. Please try again.", statusCode: HttpStatusCode.Conflict);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WatchSummary>> Get(Guid id, CancellationToken cancellationToken)
    {
        var watch = await database.Watches.AsNoTracking()
            .Where(watch => watch.Id == id)
            .Select(WatchSummary.Projection)
            .SingleOrDefaultAsync(cancellationToken);
        return watch is null ? NotFound() : watch;
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
                .SetProperty(watch => watch.TimeoutSeconds, request.TimeoutSeconds), cancellationToken);
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
        // Clearing the next Run also stops the scheduler claiming one it read as due just before.
        var updated = await database.Watches
            .Where(watch => watch.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(watch => watch.IsPaused, true)
                .SetProperty(watch => watch.NextRunAt, (DateTimeOffset?)null), cancellationToken);
        return updated == 0 ? NotFound() : NoContent();
    }

    [HttpPost("{id:guid}/resume")]
    public async Task<IActionResult> Resume(Guid id, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await database.Watches.AsNoTracking()
                .Where(watch => watch.Id == id)
                .Select(watch => new { watch.IsPaused, watch.Schedule, watch.JitterMinutes, watch.LastScheduledRunAt })
                .SingleOrDefaultAsync(cancellationToken);
            if (current is null)
            {
                return NotFound();
            }

            if (!current.IsPaused)
            {
                return NoContent();
            }

            // The next Run is scheduled from now, so a Watch resumed after a long pause doesn't run at once. The
            // failure streak carries on, and a set-based update leaves the Revision alone, so a Run in progress is
            // still recorded. It applies only if the schedule is still the one read, as one saved meanwhile decides
            // the next Run.
            var nextRunAt = schedules.NextRunAt(current.Schedule, current.JitterMinutes, current.LastScheduledRunAt);
            var updated = await database.Watches
                .Where(watch => watch.Id == id && watch.IsPaused
                    && watch.Schedule == current.Schedule
                    && watch.JitterMinutes == current.JitterMinutes
                    && watch.LastScheduledRunAt == current.LastScheduledRunAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(watch => watch.IsPaused, false)
                    .SetProperty(watch => watch.NextRunAt, nextRunAt)
                    .SetProperty(watch => watch.NextRunTimeZone, schedules.TimeZone.Id), cancellationToken);
            if (updated == 1)
            {
                return NoContent();
            }
        }
        return Problem("The schedule of this Watch kept changing while it was being resumed. Please try again.", statusCode: HttpStatusCode.Conflict);
    }

    [HttpPut("{id:guid}/channels")]
    public async Task<IActionResult> ChangeChannels(Guid id, ChannelsRequest request, CancellationToken cancellationToken)
    {
        var watch = await database.Watches
            .Include(watch => watch.Channels)
            .SingleOrDefaultAsync(watch => watch.Id == id, cancellationToken);
        if (watch is null)
        {
            return NotFound();
        }

        if (await FindChannels(request.ChannelIds, cancellationToken) is not { } channels)
        {
            return ChannelMissing();
        }

        // Only the join rows change, so the Watch's Revision is left alone and a Run in progress is still recorded.
        watch.Channels.Clear();
        foreach (var channel in channels)
        {
            watch.Channels.Add(channel);
        }
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The Watch or a chosen Channel was deleted since they were read.
            return Problem("The Watch or one of the chosen Channels no longer exists.", statusCode: HttpStatusCode.Conflict);
        }
        return NoContent();
    }

    [HttpGet("{id:guid}/runs")]
    public async Task<ActionResult<IReadOnlyList<Run>>> History(Guid id, CancellationToken cancellationToken,
        [FromQuery] int limit = 50, [FromQuery] long? before = null)
    {
        if (limit is < 1 or > 100 || before is <= 0)
        {
            return Problem("History limit must be between 1 and 100; before must be a positive Run ID.", statusCode: HttpStatusCode.BadRequest);
        }

        if (!await database.Watches.AnyAsync(watch => watch.Id == id, cancellationToken))
        {
            return NotFound();
        }

        return await database.Runs.AsNoTracking()
            .Where(run => run.WatchId == id)
            .Where(run => before == null || run.Id < before)
            .OrderByDescending(run => run.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    [HttpPost("{id:guid}/runs")]
    public async Task<ActionResult<Run>> RunNow(Guid id)
    {
        if (!runningWatches.TryStart(id))
        {
            var detail = runningWatches.IsWaitingForSlot(id)
                ? "This Watch is waiting for a free slot to run."
                : "This Watch is already running.";
            return Problem(detail, statusCode: HttpStatusCode.Conflict);
        }

        try
        {
            await runSlots.WaitAsync(applicationLifetime.ApplicationStopping);
            runningWatches.MarkExecuting(id);

            RunExecution execution;
            try
            {
                execution = await runExecutor.ExecuteAsync(id, applicationLifetime.ApplicationStopping);
            }
            finally
            {
                runSlots.Release();
            }

            return execution.Status switch
            {
                RunExecutionStatus.Recorded => Ok(execution.Run),
                RunExecutionStatus.WatchNotFound => NotFound(),
                RunExecutionStatus.NoScriptVersion => Problem("This Watch has no current Script version.", statusCode: HttpStatusCode.Conflict),
                RunExecutionStatus.Deleted => Problem("The Watch was deleted while this Run was in progress, so this Run was not recorded.", statusCode: HttpStatusCode.Conflict),
                _ => Problem("Another Run of this Watch finished first, so this Run was not recorded.", statusCode: HttpStatusCode.Conflict)
            };
        }
        finally
        {
            runningWatches.Finish(id);
        }
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

    /// <summary>Builds the requested schedule, or explains why it can't be used.</summary>
    private bool TryBuildSchedule(
        ScheduleRequest request,
        [NotNullWhen(true)] out Schedule? schedule,
        [NotNullWhen(false)] out ObjectResult? problem)
    {
        schedule = request.ScheduleKind switch
        {
            ScheduleKind.Interval => new Schedule { Kind = ScheduleKind.Interval, Every = request.Every, Unit = request.Unit },
            ScheduleKind.Daily when request.TimeOfDay is { } timeOfDay => Schedule.DailyAt(timeOfDay),
            ScheduleKind.Daily => new Schedule { Kind = ScheduleKind.Daily },
            ScheduleKind.Cron => Schedule.FromCron(request.Cron ?? ""),
            _ => null
        };
        var message = schedule is null
            ? "Choose an interval, daily or cron schedule."
            : schedules.Validate(schedule);
        if (message is null && request.JitterMinutes is < 0 or > WatchSchedules.MaximumJitterMinutes)
        {
            message = $"Jitter must be between 0 and {WatchSchedules.MaximumJitterMinutes} minutes.";
        }
        problem = message is null ? null : Problem(message, statusCode: HttpStatusCode.BadRequest);
        return problem is null;
    }
}

public sealed record WatchSummary(
    Guid Id,
    string Name,
    string NotificationUrl,
    Guid? CurrentScriptVersionId,
    string? Baseline,
    WatchStatus Status,
    bool IsPaused,
    int FailureStreak,
    int TimeoutSeconds,
    Run? LastRun,
    Schedule Schedule,
    int JitterMinutes,
    DateTimeOffset? NextRunAt,
    IReadOnlyList<Guid> ChannelIds)
{
    /// <summary>Maps a Watch in a database query; <see cref="From"/> applies the same mapping in memory.</summary>
    public static readonly Expression<Func<Watch, WatchSummary>> Projection = watch => new WatchSummary(
        watch.Id,
        watch.Name,
        watch.NotificationUrl,
        watch.CurrentScriptVersionId,
        watch.Baseline,
        // Inline rather than a method call, so EF translates it to SQL. A failure streak outranks Pending, since a
        // Watch whose Script never Succeeded needs attention too.
        Status: watch.IsPaused ? WatchStatus.Paused
            : watch.FailureStreak > 0 ? WatchStatus.Failing
            : watch.Baseline == null ? WatchStatus.Pending
            : WatchStatus.Healthy,
        watch.IsPaused,
        watch.FailureStreak,
        watch.TimeoutSeconds,
        watch.Runs.OrderByDescending(run => run.Id).FirstOrDefault(),
        watch.Schedule,
        watch.JitterMinutes,
        watch.NextRunAt,
        watch.Channels.Select(channel => channel.Id).ToList());

    private static readonly Func<Watch, WatchSummary> CompiledProjection = Projection.Compile();

    public static WatchSummary From(Watch watch) => CompiledProjection(watch);
}

public sealed class ChannelsRequest
{
    public required IReadOnlyList<Guid> ChannelIds { get; init; }
}

public sealed class EditWatchRequest
{
    public required string Name { get; init; }
    public required string NotificationUrl { get; init; }
    public required int TimeoutSeconds { get; init; }
}

public sealed class CreateWatchRequest : ScheduleRequest
{
    public required string Name { get; init; }
    public required string NotificationUrl { get; init; }
    public required IFormFile Script { get; init; }
    public List<Guid> ChannelIds { get; init; } = [];
}

public class ScheduleRequest
{
    public ScheduleKind? ScheduleKind { get; init; }
    /// <summary>For an interval schedule, how many <see cref="Unit"/>s apart its Runs are.</summary>
    public int? Every { get; init; }
    public IntervalUnit? Unit { get; init; }
    /// <summary>For a daily schedule, the time of day in the install's time zone.</summary>
    public TimeOnly? TimeOfDay { get; init; }
    public string? Cron { get; init; }
    public int JitterMinutes { get; init; }
}
