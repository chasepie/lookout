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
        if (name.Length is < 1 or > 200)
        {
            return Problem("Name must contain between 1 and 200 characters.", statusCode: HttpStatusCode.BadRequest);
        }

        var notificationUrl = request.NotificationUrl.Trim();
        if (!Uri.TryCreate(notificationUrl, UriKind.Absolute, out var parsedUrl)
            || parsedUrl.Scheme is not ("http" or "https") || notificationUrl.Length > 2048)
        {
            return Problem("Notification URL must be an HTTP or HTTPS URL.", statusCode: HttpStatusCode.BadRequest);
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

        var watch = new Watch
        {
            Name = name,
            NotificationUrl = notificationUrl,
            Schedule = schedule,
            JitterMinutes = request.JitterMinutes,
            NextRunAt = schedules.NextRunAt(schedule, request.JitterMinutes),
            NextRunTimeZone = schedules.TimeZone.Id
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
        await database.SaveChangesAsync(cancellationToken);

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
                    .SetProperty(watch => watch.NextRunAt, nextRunAt)
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
            return Problem(runningWatches.IsWaitingForSlot(id) ? "This Watch is waiting for a free slot to run." : "This Watch is already running.",
                statusCode: HttpStatusCode.Conflict);
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
                _ => Problem("Another Run of this Watch finished first, so this Run was not recorded.", statusCode: HttpStatusCode.Conflict)
            };
        }
        finally
        {
            runningWatches.Finish(id);
        }
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
    Run? LastRun,
    Schedule Schedule,
    int JitterMinutes,
    DateTimeOffset? NextRunAt)
{
    /// <summary>Maps a Watch in a database query; <see cref="From"/> applies the same mapping in memory.</summary>
    public static readonly Expression<Func<Watch, WatchSummary>> Projection = watch => new WatchSummary(
        watch.Id,
        watch.Name,
        watch.NotificationUrl,
        watch.CurrentScriptVersionId,
        watch.Baseline,
        watch.Runs.OrderByDescending(run => run.Id).FirstOrDefault(),
        watch.Schedule,
        watch.JitterMinutes,
        watch.NextRunAt);

    private static readonly Func<Watch, WatchSummary> CompiledProjection = Projection.Compile();

    public static WatchSummary From(Watch watch) => CompiledProjection(watch);
}

public sealed class CreateWatchRequest : ScheduleRequest
{
    public required string Name { get; init; }
    public required string NotificationUrl { get; init; }
    public required IFormFile Script { get; init; }
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
