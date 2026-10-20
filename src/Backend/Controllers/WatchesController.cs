using System.Linq.Expressions;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace ChasePie.Lookout.Backend.Controllers;

[ApiController]
[Route("api/watches")]
public sealed class WatchesController(
    LookoutDbContext database,
    IRunnerClient runner,
    TimeProvider timeProvider,
    RunningWatches runningWatches,
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

        using var reader = new StreamReader(request.Script.OpenReadStream());
        var source = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(source))
        {
            return Problem("Script source must not be empty.", statusCode: HttpStatusCode.BadRequest);
        }

        var watch = new Watch
        {
            Name = name,
            NotificationUrl = notificationUrl
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
            return Problem("This Watch is already running.", statusCode: HttpStatusCode.Conflict);
        }

        try
        {
            return await ExecuteRun(id, applicationLifetime.ApplicationStopping);
        }
        finally
        {
            runningWatches.Finish(id);
        }
    }

    private async Task<ActionResult<Run>> ExecuteRun(Guid id, CancellationToken cancellationToken)
    {
        var watch = await database.Watches.Include(watch => watch.CurrentScriptVersion)
            .SingleOrDefaultAsync(watch => watch.Id == id, cancellationToken);
        if (watch is null)
        {
            return NotFound();
        }

        var script = watch.CurrentScriptVersion;
        if (script is null)
        {
            return Problem("This Watch has no current Script version.", statusCode: HttpStatusCode.Conflict);
        }
        var startedAt = timeProvider.GetUtcNow();
        var runRequest = new RunnerRequest(
            Source: script.Source,
            FileKind: script.FileKind,
            Context: new ScriptContext(watch.NotificationUrl));
        var result = await runner.ExecuteAsync(runRequest, cancellationToken);

        var evaluation = RunEvaluation.Evaluate(watch.Baseline, result.Outcome, result.Content, result.ErrorMessage);
        var run = new Run
        {
            WatchId = id,
            ScriptVersionId = script.Id,
            Outcome = evaluation.Outcome,
            Content = result.Content,
            Details = result.Details,
            ErrorMessage = evaluation.ErrorMessage,
            DurationMs = result.DurationMs,
            StartedAt = startedAt,
            FinishedAt = timeProvider.GetUtcNow(),
            IsChange = evaluation.IsChange
        };
        watch.Baseline = evaluation.Baseline;
        watch.Revision++;
        database.Runs.Add(run);
        try
        {
            // The Run has finished, so record it even if shutdown started while the runner was replying.
            await database.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problem("Another Run of this Watch finished first, so this Run was not recorded.", statusCode: HttpStatusCode.Conflict);
        }

        return Ok(run);
    }
}

public sealed record WatchSummary(Guid Id, string Name, string NotificationUrl, Guid? CurrentScriptVersionId,
    string? Baseline, Run? LastRun)
{
    /// <summary>Maps a Watch in a database query; <see cref="From"/> applies the same mapping in memory.</summary>
    public static readonly Expression<Func<Watch, WatchSummary>> Projection = watch => new WatchSummary(
        watch.Id,
        watch.Name,
        watch.NotificationUrl,
        watch.CurrentScriptVersionId,
        watch.Baseline,
        watch.Runs.OrderByDescending(run => run.Id).FirstOrDefault());

    private static readonly Func<Watch, WatchSummary> CompiledProjection = Projection.Compile();

    public static WatchSummary From(Watch watch) => CompiledProjection(watch);
}

public sealed class CreateWatchRequest
{
    public required string Name { get; init; }
    public required string NotificationUrl { get; init; }
    public required IFormFile Script { get; init; }
}
