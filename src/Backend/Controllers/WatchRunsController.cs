using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

[ApiController]
[Route("Watches/{watchId:int}/runs")]
public class WatchRunsController : ControllerBase
{
    private readonly LookoutDbContext _dbContext;

    public WatchRunsController(LookoutDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpPost]
    public async Task<ActionResult<WatchRun>> Create(int watchId, CancellationToken cancellationToken)
    {
        var watchEntity = await _dbContext.Watches
            .SingleOrDefaultAsync(w => w.Id == watchId, cancellationToken);
        if (watchEntity is null)
        {
            return NotFound();
        }

        var watchRun = new WatchRun(watchEntity);
        _dbContext.WatchRuns.Add(watchRun);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { watchId, runId = watchRun.Id }, watchRun);
    }

    [HttpGet]
    public async Task<ActionResult<List<WatchRun>>> GetAll(int watchId, CancellationToken cancellationToken)
    {
        var watchExists = await _dbContext.Watches
            .AnyAsync(w => w.Id == watchId, cancellationToken);
        if (!watchExists)
        {
            return NotFound();
        }

        var watchRuns = await _dbContext.WatchRuns
            .Where(wr => wr.WatchId == watchId)
            .ToListAsync(cancellationToken);
        return watchRuns;
    }

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
}
