using System.Globalization;
using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WatchesController(
    LookoutDbContext dbContext,
    WatchScriptManager scriptManager,
    UpdatesNotifier notifier) : ControllerBase
{
    private readonly LookoutDbContext _dbContext = dbContext;
    private readonly WatchScriptManager _scriptManager = scriptManager;
    private readonly UpdatesNotifier _notifier = notifier;

    /// <summary>
    /// A watch, with its latest runs only if asked for: its page reads them from its run log instead.
    /// </summary>
    /// <param name="recentRuns">How many of its latest runs to include, from 0 to <see cref="WatchSummary.RecentRunCount"/>.</param>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<WatchSummary>> Get(int id, CancellationToken cancellationToken, int recentRuns = 0)
    {
        if (recentRuns is < 0 or > WatchSummary.RecentRunCount)
        {
            return BadRequest($"recentRuns must be from 0 to {WatchSummary.RecentRunCount}.");
        }

        var watches = await WatchSummary.ListAsync(
            _dbContext.Watches.Where(w => w.Id == id),
            recentRuns,
            DateTime.UtcNow,
            cancellationToken);
        return watches.Count == 0 ? NotFound() : watches[0];
    }

    /// <summary>
    /// Lists every watch by name, ignoring case and accents, as a person would sort them.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<WatchSummary>>> GetAll(CancellationToken cancellationToken)
    {
        // Sorted here rather than in SQL: SQLite compares bytes, putting "Zebra" before "apple"
        var watches = await WatchSummary.ListAsync(
            _dbContext.Watches, WatchSummary.RecentRunCount, DateTime.UtcNow, cancellationToken);
        return watches
            .OrderBy(w => w.Name, NameOrder)
            .ThenBy(w => w.Id)
            .ToList();
    }

    private static readonly StringComparer NameOrder =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);

    /// <summary>
    /// Creates a watch with its script. Send the watch's fields and the <c>script</c> file as multipart form data.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(WatchScriptManager.MaxUploadRequestBytes)]
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

        await _notifier.WatchChangedAsync(watchEntity.Id);
        return CreatedAtAction(nameof(Get), new { id = watchEntity.Id }, watchEntity);
    }

    [HttpPut("{id:int}")]
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
        await _notifier.WatchChangedAsync(watchEntity.Id);
        return watchEntity;
    }

    [HttpDelete("{id:int}")]
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
        _scriptManager.DeleteScript(watchEntity);
        await _notifier.WatchChangedAsync(watchEntity.Id);
        return watchEntity;
    }

    /// <summary>
    /// Replaces the watch's script.
    /// </summary>
    [HttpPost("{id:int}/script")]
    [RequestSizeLimit(WatchScriptManager.MaxUploadRequestBytes)]
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

        await _notifier.WatchChangedAsync(watchEntity.Id);
        return watchEntity;
    }
}
