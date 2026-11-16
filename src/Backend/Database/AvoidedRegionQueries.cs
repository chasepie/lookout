using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Database;

/// <summary>
/// The regions watches avoid, as the run worker and <see cref="VpnServices.Vpn"/> read them.
/// </summary>
internal static class AvoidedRegionQueries
{
    /// <summary>
    /// The regions each of <paramref name="watchIds"/> avoids, in one query.
    /// </summary>
    public static async Task<ILookup<int, string>> GetAvoidedRegionsAsync(
        this LookoutDbContext dbContext, IReadOnlyCollection<int> watchIds, CancellationToken cancellationToken)
    {
        var avoided = await dbContext.AvoidedRegions
            .Where(a => watchIds.Contains(a.WatchId))
            .Select(a => new { a.WatchId, a.Region })
            .ToListAsync(cancellationToken);
        return avoided.ToLookup(a => a.WatchId, a => a.Region);
    }

    /// <summary>
    /// The regions the watch avoids.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetAvoidedRegionsAsync(
        this LookoutDbContext dbContext, int watchId, CancellationToken cancellationToken) =>
        [.. (await dbContext.GetAvoidedRegionsAsync([watchId], cancellationToken))[watchId]];
}
