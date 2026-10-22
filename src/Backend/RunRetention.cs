using ChasePie.Lookout.Database;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend;

/// <summary>
/// Keeps the database small by pruning Runs older than the retention period in Settings, at startup and then periodically.
/// Changes and each Watch's latest Run are kept forever, as are Watches' Baselines, which live on the Watch rather
/// than its Runs.
/// </summary>
public sealed partial class RunRetention(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<RunRetention> logger)
    : BackgroundService
{
    /// <summary>How often Runs are pruned; a Run outlives the retention period by at most this long.</summary>
    public static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PruneInterval, timeProvider);
        try
        {
            do
            {
                try
                {
                    await Prune(stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    LogPruneFailed(logger, error);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Lookout is stopping; the next startup prunes what this one didn't.
        }
    }

    private async Task Prune(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        var retentionDays = await database.Settings.Select(settings => settings.RetentionDays).SingleAsync(cancellationToken);
        var cutoff = timeProvider.GetUtcNow().AddDays(-retentionDays);
        // EF can't compare DateTimeOffsets in SQLite, which stores them as text. Lookout stores UTC throughout, so every
        // value has the same offset and sorts as text in time order. Each Watch keeps its latest Run, however old, so
        // the Watch list still shows it. Deleting a Run deletes its debug output too.
        await database.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM "Runs"
            WHERE NOT "IsChange" AND "FinishedAt" < {cutoff}
                AND "Id" NOT IN (SELECT MAX("Id") FROM "Runs" GROUP BY "WatchId")
            """, cancellationToken);
    }

    [LoggerMessage(LogLevel.Error, "Could not prune old Runs.")]
    private static partial void LogPruneFailed(ILogger logger, Exception error);
}
