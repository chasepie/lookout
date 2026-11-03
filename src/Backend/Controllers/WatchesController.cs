using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

[ApiController]
[Route("[controller]")]
public class WatchesController : ControllerBase
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

    /// <summary>
    /// Creates a watch with its script. Send the watch's fields and the <c>script</c> file as multipart form data.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Watch>> Create(
        [FromForm] CreateWatchDto watch, IFormFile script, CancellationToken cancellationToken)
    {
        var watchEntity = new Watch(watch);
        _dbContext.Watches.Add(watchEntity);

        // Saves the watch and its script together
        var errorMessage = await _scriptManager.SaveScriptAsync(watchEntity, script, cancellationToken);
        if (errorMessage is not null)
        {
            return BadRequest(errorMessage);
        }

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

    /// <summary>
    /// Replaces the watch's script.
    /// </summary>
    [HttpPost("{id}/script")]
    public async Task<ActionResult<Watch>> UploadScript(
        int id, IFormFile script, CancellationToken cancellationToken)
    {
        var watchEntity = await _dbContext.Watches
            .SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (watchEntity is null)
        {
            return NotFound();
        }

        var errorMessage = await _scriptManager.SaveScriptAsync(watchEntity, script, cancellationToken);
        if (errorMessage is not null)
        {
            return BadRequest(errorMessage);
        }

        return watchEntity;
    }
}
