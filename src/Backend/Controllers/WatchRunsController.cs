using System.Diagnostics;
using System.Net;
using ChasePie.Lookout.Backend.Running;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

public sealed record DebugOutputSummary(string? ConsoleOutput, bool HasScreenshot);

public sealed class TestRunRequest
{
    public required IFormFile Script { get; init; }
}

/// <summary>A Watch's Runs: starting one now, its history and debug output, and Test runs of an uploaded Script.</summary>
[ApiController]
[Route("api/watches")]
public sealed class WatchRunsController(LookoutDbContext database, Runs runs) : LookoutControllerBase
{
    [HttpPost("{id:guid}/test-runs")]
    [RequestSizeLimit(128 * 1024)]
    public async Task<ActionResult<TestRun>> RunTest(Guid id, [FromForm] TestRunRequest request, CancellationToken cancellationToken)
    {
        var (script, scriptProblem) = await UploadedScript.ReadAsync(request.Script, cancellationToken);
        if (script is null)
        {
            return Problem(scriptProblem!, statusCode: HttpStatusCode.BadRequest);
        }

        // Leaving the page cancels the request, which stops the Script.
        var testRun = await runs.TestAsync(id, script.Source, script.FileKind, cancellationToken);
        return testRun is null ? NotFound() : testRun;
    }

    [HttpGet("{id:guid}/runs")]
    public async Task<ActionResult<IReadOnlyList<Run>>> History(Guid id, CancellationToken cancellationToken,
        [FromQuery] int limit = 50, [FromQuery] long? before = null)
    {
        if (limit is < 1 or > 100 || before is <= 0)
        {
            return Problem(
                detail: "History limit must be between 1 and 100; before must be a positive Run ID.",
                statusCode: HttpStatusCode.BadRequest);
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

    /// <summary>A Known failure or Errored Run's console output, and whether it has a screenshot, while it is one that keeps them.</summary>
    [HttpGet("{id:guid}/runs/{runId:long}/debug-output")]
    public async Task<ActionResult<DebugOutputSummary>> DebugOutput(Guid id, long runId, CancellationToken cancellationToken)
    {
        var debugOutput = await DebugOutputOf(id, runId)
            .Select(item => new DebugOutputSummary(item.ConsoleOutput, item.Screenshot != null))
            .SingleOrDefaultAsync(cancellationToken);
        return debugOutput is null ? NotFound() : debugOutput;
    }

    [HttpGet("{id:guid}/runs/{runId:long}/screenshot")]
    public async Task<IActionResult> Screenshot(Guid id, long runId, CancellationToken cancellationToken)
    {
        var screenshot = await DebugOutputOf(id, runId)
            .Select(item => item.Screenshot)
            .SingleOrDefaultAsync(cancellationToken);
        if (screenshot is null)
        {
            return NotFound();
        }
        // A Run's screenshot never changes, so reopening its debug output needn't download it again.
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return File(screenshot, "image/png");
    }

    private IQueryable<RunDebugOutput> DebugOutputOf(Guid watchId, long runId) =>
        database.RunDebugOutputs.AsNoTracking()
            .Where(item => item.RunId == runId && item.Run!.WatchId == watchId);

    [HttpPost("{id:guid}/runs")]
    public async Task<ActionResult<Run>> RunNow(Guid id)
    {
        var admission = runs.Run(id);
        if (admission is Refused { Reason: RunRefusal.WaitingForSlot })
        {
            return Problem(
                detail: "This Watch is waiting for a free slot to run.",
                statusCode: HttpStatusCode.Conflict);
        }

        if (admission is Refused)
        {
            return Problem(
                detail: "This Watch is already running.",
                statusCode: HttpStatusCode.Conflict);
        }

        if (admission is Admitted { Finished: var finished })
        {
            // Not cancelled by the request: a Run the user started carries on if they leave the page.
            var execution = await finished;
            return execution.Status switch
            {
                RunExecutionStatus.Recorded => Ok(execution.Run),
                RunExecutionStatus.WatchNotFound => NotFound(),
                RunExecutionStatus.NoScriptVersion => Problem(
                    detail: "This Watch has no current Script version.",
                    statusCode: HttpStatusCode.Conflict),
                RunExecutionStatus.Deleted => Problem(
                    detail: "The Watch was deleted while this Run was in progress, so this Run was not recorded.",
                    statusCode: HttpStatusCode.Conflict),
                RunExecutionStatus.ScriptReplaced => Problem(
                    detail: "A new Script version was uploaded while this Run was in progress, so this Run was not recorded.",
                    statusCode: HttpStatusCode.Conflict),
                _ => Problem(
                    detail: "Another Run of this Watch finished first, so this Run was not recorded.",
                    statusCode: HttpStatusCode.Conflict)
            };
        }

        throw new UnreachableException();
    }
}
