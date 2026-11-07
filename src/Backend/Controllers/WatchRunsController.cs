using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

[ApiController]
[Route("api/Watches/{watchId:int}/runs")]
public class WatchRunsController(LookoutDbContext dbContext, UpdatesNotifier notifier) : ControllerBase
{
    private readonly LookoutDbContext _dbContext = dbContext;
    private readonly UpdatesNotifier _notifier = notifier;

    /// <summary>
    /// Creates a run for <see cref="WatchRunWorker"/> to pick up. Poll the returned run for its status.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<WatchRun>> Create(int watchId, CancellationToken cancellationToken)
    {
        var watchEntity = await _dbContext.Watches
            .SingleOrDefaultAsync(w => w.Id == watchId, cancellationToken);
        if (watchEntity is null)
        {
            return NotFound();
        }

        if (WatchScriptsDirectory.GetStoredFileName(watchEntity) is null)
        {
            return BadRequest("Upload a script before running this watch.");
        }

        var watchRun = new WatchRun(watchEntity);
        _dbContext.WatchRuns.Add(watchRun);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _notifier.RunUpdatedAsync(watchRun);
        return CreatedAtAction(nameof(Get), new { watchId, runId = watchRun.Id }, watchRun);
    }

    /// <summary>
    /// Lists a watch's runs, newest first, without their screenshots. Page back through older runs by passing the
    /// last ID received as <paramref name="before"/>.
    /// </summary>
    /// <param name="before">Only runs with a lower ID than this, which are older.</param>
    /// <param name="limit">How many runs at most, from 1 to <see cref="MaxPageSize"/>.</param>
    [HttpGet]
    public async Task<ActionResult<List<WatchRunSummary>>> GetAll(
        int watchId, int? before, CancellationToken cancellationToken, int limit = DefaultPageSize)
    {
        if (limit is < 1 or > MaxPageSize)
        {
            return BadRequest($"limit must be from 1 to {MaxPageSize}.");
        }

        var watchExists = await _dbContext.Watches
            .AnyAsync(w => w.Id == watchId, cancellationToken);
        if (!watchExists)
        {
            return NotFound();
        }

        var runs = _dbContext.WatchRuns.Where(wr => wr.WatchId == watchId);
        if (before is { } beforeId)
        {
            runs = runs.Where(wr => wr.Id < beforeId);
        }

        return await runs
            .OrderByDescending(wr => wr.Id)
            .Take(limit)
            .Select(WatchRunSummary.Projection)
            .ToListAsync(cancellationToken);
    }

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    [HttpGet("{runId:int}")]
    public async Task<ActionResult<WatchRun>> Get(int watchId, int runId, CancellationToken cancellationToken)
    {
        var watchRun = await _dbContext.WatchRuns
            .SingleOrDefaultAsync(wr => wr.WatchId == watchId && wr.Id == runId, cancellationToken);
        if (watchRun is null)
        {
            return NotFound();
        }

        return watchRun;
    }

    [HttpGet("latest")]
    public async Task<ActionResult<WatchRun>> GetLatest(int watchId, CancellationToken cancellationToken)
    {
        var watchRun = await _dbContext.WatchRuns
            .Where(wr => wr.WatchId == watchId)
            .OrderByDescending(wr => wr.CreatedTime)
            .FirstOrDefaultAsync(cancellationToken);
        if (watchRun is null)
        {
            return NotFound();
        }

        return watchRun;
    }

    /// <summary>
    /// Deletes a run. A run that is in progress can't be deleted until it finishes.
    /// </summary>
    [HttpDelete("{runId:int}")]
    public async Task<ActionResult<WatchRun>> Delete(int watchId, int runId, CancellationToken cancellationToken)
    {
        var watchRun = await _dbContext.WatchRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(wr => wr.WatchId == watchId && wr.Id == runId, cancellationToken);
        if (watchRun is null)
        {
            return NotFound();
        }

        // Conditional, in case the worker started the run since it was read
        var deleted = await _dbContext.WatchRuns
            .Where(wr => wr.Id == runId && wr.Status != WatchRunStatus.Running)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            return Conflict(RunInProgressMessage);
        }

        await _notifier.RunsDeletedAsync(watchId, [runId]);
        return watchRun;
    }

    /// <summary>
    /// Deletes all of a watch's runs, except any that are in progress.
    /// </summary>
    [HttpDelete]
    public async Task<ActionResult<DeleteRunsResult>> DeleteAll(int watchId, CancellationToken cancellationToken)
    {
        var watchExists = await _dbContext.Watches
            .AnyAsync(w => w.Id == watchId, cancellationToken);
        if (!watchExists)
        {
            return NotFound();
        }

        var runIds = await _dbContext.WatchRuns
            .Where(wr => wr.WatchId == watchId && wr.Status != WatchRunStatus.Running)
            .Select(wr => wr.Id)
            .ToListAsync(cancellationToken);
        await _dbContext.WatchRuns
            .Where(wr => runIds.Contains(wr.Id) && wr.Status != WatchRunStatus.Running)
            .ExecuteDeleteAsync(cancellationToken);

        var keptRunning = await _dbContext.WatchRuns
            .CountAsync(wr => wr.WatchId == watchId, cancellationToken);

        await _notifier.RunsDeletedAsync(watchId, runIds);
        return new DeleteRunsResult(runIds.Count, keptRunning);
    }

    // Deleting a running run would make the worker's final save fail
    private const string RunInProgressMessage = "This run is in progress. Delete it after it finishes.";
}

/// <summary>
/// The outcome of deleting all of a watch's runs.
/// </summary>
/// <param name="Deleted">How many runs were deleted.</param>
/// <param name="KeptRunning">How many runs were kept because they're in progress.</param>
public sealed record DeleteRunsResult(int Deleted, int KeptRunning);
