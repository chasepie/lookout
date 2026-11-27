using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Controllers;

/// <summary>
/// The regions a watch avoids, which only its runs' known failures add, so the Owner can only clear them. They're listed
/// with the watch, in <see cref="WatchSummary.AvoidedRegions"/>.
/// </summary>
[ApiController]
[Route("api/Watches/{watchId:int}/avoided-regions")]
public class AvoidedRegionsController(LookoutDbContext dbContext, UpdatesNotifier notifier) : ControllerBase
{
    private readonly LookoutDbContext _dbContext = dbContext;
    private readonly UpdatesNotifier _notifier = notifier;

    /// <summary>
    /// Clears a region the watch avoids, so its runs may run there again.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int watchId, int id, CancellationToken cancellationToken)
    {
        var deleted = await _dbContext.AvoidedRegions
            .Where(a => a.WatchId == watchId && a.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            return NotFound();
        }

        await _notifier.WatchChangedAsync(watchId);
        return NoContent();
    }
}
