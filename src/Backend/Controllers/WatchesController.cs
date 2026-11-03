using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

[ApiController]
[Route("[controller]")]
public partial class WatchesController : ControllerBase
{
    private readonly LookoutDbContext _dbContext;
    private readonly WatchScriptManager _scriptManager;

    public WatchesController(LookoutDbContext dbContext, WatchScriptManager scriptManager)
    {
        _dbContext = dbContext;
        _scriptManager = scriptManager;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Watch>> Get(int id, CancellationToken cancellationToken)
    {
        var watch = await _dbContext.Watches
            .Include(w => w.WatchRuns
                .OrderByDescending(wr => wr.CreatedTime)
                .Take(25))
            .SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (watch is null)
        {
            return NotFound();
        }

        return watch;
    }

    [HttpGet]
    public async Task<ActionResult<List<Watch>>> GetAll(CancellationToken cancellationToken)
    {
        var watches = await _dbContext.Watches
            .Include(w => w.WatchRuns
                .OrderByDescending(wr => wr.CreatedTime)
                .Take(1))
            .ToListAsync(cancellationToken);
        return watches;
    }

    [HttpPost]
    public async Task<ActionResult<Watch>> Create(CreateWatchDto watch, CancellationToken cancellationToken)
    {
        var watchEntity = new Watch(watch);
        _dbContext.Watches.Add(watchEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = watchEntity.Id }, watchEntity);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Watch>> Update(int id, CreateWatchDto watch, CancellationToken cancellationToken)
    {
        var watchEntity = await _dbContext.Watches
            .SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (watchEntity is null)
        {
            return NotFound();
        }

        watchEntity.Update(watch);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return watchEntity;
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<Watch>> Delete(int id, CancellationToken cancellationToken)
    {
        var watchEntity = await _dbContext.Watches
            .SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (watchEntity is null)
        {
            return NotFound();
        }

        _dbContext.Watches.Remove(watchEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return watchEntity;
    }

    [HttpPost("{id}/script")]
    public async Task<ActionResult> UploadScript(
        int id, IFormFile script, CancellationToken cancellationToken)
    {
        var watchEntity = await _dbContext.Watches
            .SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (watchEntity is null)
        {
            return NotFound();
        }

        var (successful, errorMessage) = await _scriptManager.SaveScriptAsync(
            watchEntity.Id, script, cancellationToken);
        if (!successful)
        {
            return BadRequest(errorMessage);
        }

        return Ok();
    }
}
