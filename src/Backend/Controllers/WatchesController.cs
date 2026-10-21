using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Backend.Events;
using ChasePie.Lookout.Backend.Runner;
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
    IRunnerClient runner,
    WatchSchedules schedules,
    RunningWatches runningWatches,
    RunSlots runSlots,
    WatchEvents watchEvents,
    IHostApplicationLifetime applicationLifetime,
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

        var (script, scriptProblem) = await ReadScriptAsync(request.Script, cancellationToken);
        if (script is null)
        {
            return scriptProblem!;
        }

        if (!TryBuildSchedule(request, out var schedule, out var scheduleProblem))
        {
            return scheduleProblem;
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
                return WithRunning(await database.Watches.AsNoTracking()
                    .Where(watch => watch.Id == id)
                    .Select(WatchSummary.Projection)
                    .SingleAsync(cancellationToken));
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
            .Where(watch => watch.Id == id && !watch.IsPaused)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(watch => watch.IsPaused, true)
                .SetProperty(watch => watch.NextRunAt, (DateTimeOffset?)null), cancellationToken);
        if (updated == 1)
        {
            await watchEvents.StatusChangedAsync(id, WatchStatus.Paused);
            return NoContent();
        }
        return await database.Watches.AnyAsync(watch => watch.Id == id, cancellationToken) ? NoContent() : NotFound();
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
                if (await WatchSummary.StatusAsync(database.Watches, id, CancellationToken.None) is { } resumed)
                {
                    await watchEvents.StatusChangedAsync(id, resumed);
                }
                return NoContent();
            }
        }
        return Problem("The schedule of this Watch kept changing while it was being resumed. Please try again.", statusCode: HttpStatusCode.Conflict);
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

    [HttpPost("{id:guid}/script-versions")]
    [RequestSizeLimit(128 * 1024)]
    public async Task<ActionResult<ScriptVersionSummary>> UploadScript(Guid id, [FromForm] UploadScriptRequest request,
        CancellationToken cancellationToken)
    {
        var (script, scriptProblem) = await ReadScriptAsync(request.Script, cancellationToken);
        if (script is null)
        {
            return scriptProblem!;
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        if (!await database.Watches.AnyAsync(watch => watch.Id == id, cancellationToken))
        {
            return NotFound();
        }

        // The transaction holds SQLite's write lock from the start, so no other upload can take the same number.
        var latestNumber = await database.ScriptVersions
            .Where(version => version.WatchId == id)
            .MaxAsync(version => (int?)version.Number, cancellationToken);
        var version = new ScriptVersion
        {
            WatchId = id,
            Number = (latestNumber ?? 0) + 1,
            UploadedAt = timeProvider.GetUtcNow(),
            Source = script.Source,
            FileKind = script.FileKind
        };
        database.ScriptVersions.Add(version);
        await database.SaveChangesAsync(cancellationToken);

        // Advancing the Revision discards a Run of the replaced version still in progress, which would otherwise set
        // the Baseline from the old Script's content. A set-based update leaves the Watch's other settings alone.
        await database.Watches
            .Where(watch => watch.Id == id)
            .ExecuteUpdateAsync(setters =>
            {
                setters
                    .SetProperty(watch => watch.CurrentScriptVersionId, version.Id)
                    .SetProperty(watch => watch.Revision, watch => watch.Revision + 1);
                // A new Script may report its content differently, so by default it starts afresh, as Pending.
                if (!request.KeepBaseline)
                {
                    setters
                        .SetProperty(watch => watch.Baseline, (string?)null)
                        .SetProperty(watch => watch.FailureStreak, 0);
                }
            }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return StatusCode(StatusCodes.Status201Created,
            new ScriptVersionSummary(version.Id, version.Number, version.FileKind, version.UploadedAt, IsCurrent: true));
    }

    [HttpGet("{id:guid}/script-versions")]
    public async Task<ActionResult<IReadOnlyList<ScriptVersionSummary>>> ListScriptVersions(Guid id, CancellationToken cancellationToken)
    {
        var currentVersionId = await database.Watches
            .Where(watch => watch.Id == id)
            .Select(watch => new { watch.CurrentScriptVersionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (currentVersionId is null)
        {
            return NotFound();
        }

        return await database.ScriptVersions.AsNoTracking()
            .Where(version => version.WatchId == id)
            .OrderByDescending(version => version.Number)
            .Select(version => new ScriptVersionSummary(version.Id, version.Number, version.FileKind, version.UploadedAt,
                version.Id == currentVersionId.CurrentScriptVersionId))
            .ToListAsync(cancellationToken);
    }

    [HttpGet("{id:guid}/script-versions/{versionId:guid}")]
    public async Task<ActionResult<ScriptVersionSource>> GetScriptVersion(Guid id, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await database.ScriptVersions.AsNoTracking()
            .Where(version => version.Id == versionId && version.WatchId == id)
            .Select(version => new ScriptVersionSource(
                Id: version.Id,
                Number: version.Number,
                FileKind: version.FileKind,
                UploadedAt: version.UploadedAt,
                IsCurrent: database.Watches.Any(watch => watch.Id == id && watch.CurrentScriptVersionId == version.Id),
                Source: version.Source))
            .SingleOrDefaultAsync(cancellationToken);
        return version is null ? NotFound() : version;
    }

    [HttpPost("{id:guid}/test-runs")]
    [RequestSizeLimit(128 * 1024)]
    public async Task<ActionResult<TestRun>> RunTest(Guid id, [FromForm] TestRunRequest request, CancellationToken cancellationToken)
    {
        var (script, scriptProblem) = await ReadScriptAsync(request.Script, cancellationToken);
        if (script is null)
        {
            return scriptProblem!;
        }

        var watch = await database.Watches.AsNoTracking()
            .Where(watch => watch.Id == id)
            .Select(watch => new { watch.NotificationUrl, watch.TimeoutSeconds })
            .SingleOrDefaultAsync(cancellationToken);
        if (watch is null)
        {
            return NotFound();
        }

        // A Test run takes a slot like any Run, since it uses the browser too, but it isn't one of the Watch's Runs: it
        // doesn't make the Watch Running, and its result is neither evaluated against the Baseline nor recorded.
        // Leaving the page cancels the request, which stops the Script.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, applicationLifetime.ApplicationStopping);
        await runSlots.WaitAsync(stopping.Token);
        RunnerResult result;
        try
        {
            result = await runner.ExecuteAsync(new RunnerRequest(
                Source: script.Source,
                FileKind: script.FileKind,
                Context: new ScriptContext(watch.NotificationUrl),
                TimeoutMs: watch.TimeoutSeconds * 1000,
                AlwaysScreenshot: true), stopping.Token);
        }
        finally
        {
            runSlots.Release();
        }

        // Shows the outcome a Run would record for the same result.
        var evaluation = RunEvaluation.Evaluate(null, 0, result.Outcome, result.Content, result.ErrorMessage);
        return new TestRun(
            Outcome: evaluation.Outcome,
            Content: result.Content,
            Details: result.Details,
            ErrorMessage: evaluation.ErrorMessage,
            DurationMs: result.DurationMs,
            Screenshot: result.Screenshot,
            ConsoleOutput: result.ConsoleOutput);
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
                RunExecutionStatus.ScriptReplaced => Problem("A new Script version was uploaded while this Run was in progress, so this Run was not recorded.", statusCode: HttpStatusCode.Conflict),
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

    private WatchSummary WithRunning(WatchSummary watch) => watch with { IsRunning = watchEvents.IsRunning(watch.Id) };

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

    /// <summary>Reads an uploaded Script, or explains why it can't be used.</summary>
    private async Task<(UploadedScript? Script, ObjectResult? Problem)> ReadScriptAsync(IFormFile file,
        CancellationToken cancellationToken)
    {
        var fileKind = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (fileKind is not (".ts" or ".js") || file.Length is < 1 or > 65536)
        {
            return (null, Problem("Upload a .ts or .js Script between 1 byte and 64 KiB.", statusCode: HttpStatusCode.BadRequest));
        }

        using var reader = new StreamReader(file.OpenReadStream());
        var source = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(source))
        {
            return (null, Problem("Script source must not be empty.", statusCode: HttpStatusCode.BadRequest));
        }

        return (new UploadedScript(source, fileKind[1..]), null);
    }

    private sealed record UploadedScript(string Source, string FileKind);

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
    /// <summary>
    /// A Watch's status, as an expression rather than a method, so EF translates it to SQL. A failure streak outranks
    /// Pending, since a Watch whose Script never Succeeded needs attention too.
    /// </summary>
    private static readonly Expression<Func<Watch, WatchStatus>> StatusProjection = watch =>
        watch.IsPaused
            ? WatchStatus.Paused
            : watch.FailureStreak > 0
                ? WatchStatus.Failing
                : watch.Baseline == null
                    ? WatchStatus.Pending
                    : WatchStatus.Healthy;

    /// <summary>Maps a Watch in a database query; <see cref="From"/> applies the same mapping in memory.</summary>
    public static readonly Expression<Func<Watch, WatchSummary>> Projection = WithStatus((watch, status) => new WatchSummary(
        watch.Id,
        watch.Name,
        watch.NotificationUrl,
        watch.CurrentScriptVersionId,
        watch.Baseline,
        status,
        watch.IsPaused,
        watch.FailureStreak,
        watch.TimeoutSeconds,
        watch.Runs.OrderByDescending(run => run.Id).FirstOrDefault(),
        watch.Schedule,
        watch.JitterMinutes,
        watch.NextRunAt,
        watch.Channels.Select(channel => channel.Id).ToList()));

    /// <summary>Whether the Watch is Running, which is shown alongside its <see cref="Status"/>.</summary>
    public bool IsRunning { get; init; }

    private static readonly Func<Watch, WatchSummary> CompiledProjection = Projection.Compile();
    private static readonly Func<Watch, WatchStatus> CompiledStatus = StatusProjection.Compile();

    public static WatchSummary From(Watch watch) => CompiledProjection(watch);

    public static WatchStatus StatusOf(Watch watch) => CompiledStatus(watch);

    /// <summary>The Watch's status as stored, or null if it doesn't exist.</summary>
    public static Task<WatchStatus?> StatusAsync(IQueryable<Watch> watches, Guid id, CancellationToken cancellationToken) =>
        watches.Where(watch => watch.Id == id)
            .Select(StatusProjection)
            .Select(status => (WatchStatus?)status)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Puts <see cref="StatusProjection"/> in the shape's status, so EF translates the whole projection.</summary>
    private static Expression<Func<Watch, WatchSummary>> WithStatus(Expression<Func<Watch, WatchStatus, WatchSummary>> shape)
    {
        var watch = shape.Parameters[0];
        var status = new ReplaceParameter(StatusProjection.Parameters[0], watch)
            .Visit(StatusProjection.Body);
        return Expression.Lambda<Func<Watch, WatchSummary>>(
            new ReplaceParameter(shape.Parameters[1], status).Visit(shape.Body), watch);
    }

    private sealed class ReplaceParameter(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? replacement : node;
    }
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

public sealed record ScriptVersionSummary(Guid Id, int Number, string FileKind, DateTimeOffset UploadedAt, bool IsCurrent);

public sealed record ScriptVersionSource(Guid Id, int Number, string FileKind, DateTimeOffset UploadedAt, bool IsCurrent,
    string Source);

/// <summary>The result of a Test run, which, unlike a Run's, has a screenshot even when it Succeeded.</summary>
public sealed record TestRun(
    RunOutcome Outcome,
    string? Content,
    string? Details,
    string? ErrorMessage,
    double DurationMs,
    string? Screenshot,
    string? ConsoleOutput);

public sealed class TestRunRequest
{
    public required IFormFile Script { get; init; }
}

public sealed class UploadScriptRequest
{
    public required IFormFile Script { get; init; }
    /// <summary>Keeps the Baseline and failure streak, for a new version that reports its content the same way.</summary>
    public bool KeepBaseline { get; init; }
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
