using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

public partial class WatchesController : ControllerBase
{
    [HttpPost("{id}/run")]
    public async Task<ActionResult<WatchRun>> Run(int id, CancellationToken cancellationToken)
    {
        var watchEntity = await _dbContext.Watches
            .SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (watchEntity is null)
        {
            return NotFound();
        }

        var watchRun = new WatchRun(watchEntity);
        _dbContext.WatchRuns.Add(watchRun);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return watchRun;
    }

    [HttpGet("{id}/runs/{runId}")]
    public async Task<ActionResult<WatchRun>> GetRun(int id, int runId, CancellationToken cancellationToken)
    {
        var watchRun = await _dbContext.WatchRuns
            .SingleOrDefaultAsync(wr => wr.WatchId == id && wr.Id == runId, cancellationToken);
        if (watchRun is null)
        {
            return NotFound();
        }

        return watchRun;
    }

    [HttpGet("{id}/runs/latest")]
    public async Task<ActionResult<WatchRun>> GetLatestRun(int id, CancellationToken cancellationToken)
    {
        var watchRun = await _dbContext.WatchRuns
            .Where(wr => wr.WatchId == id)
            .OrderByDescending(wr => wr.CreatedTime)
            .FirstOrDefaultAsync(cancellationToken);
        if (watchRun is null)
        {
            return NotFound();
        }

        return watchRun;
    }

    [HttpGet("{id}/runs")]
    public async Task<ActionResult<List<WatchRun>>> GetRuns(int id, CancellationToken cancellationToken)
    {
        var watchRuns = await _dbContext.WatchRuns
            .Where(wr => wr.WatchId == id)
            .ToListAsync(cancellationToken);
        return watchRuns;
    }
}
