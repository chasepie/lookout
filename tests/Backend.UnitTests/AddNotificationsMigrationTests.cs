using ChasePie.Lookout.Backend.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.UnitTests;

/// <summary>
/// What adding notifications does to a database that already has watches and runs.
/// </summary>
[TestClass]
public sealed class AddNotificationsMigrationTests
{
    private const string PreviousMigration = "20261109023409_AddScreenshots";

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task ExistingWatchesGetTheDefaultThresholdAndInterruptedRunsAreMarked()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        await using var dbContext = new LookoutDbContext(
            new DbContextOptionsBuilder<LookoutDbContext>().UseSqlite(connection).Options);
        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Ct);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO Watches (Id, Name, ScheduleEnabled, UseVpn, ScreenshotOnSuccess, ScreenshotOnKnownFailure, ScreenshotOnCrash)
            VALUES (1, 'Shoe stock', 0, 0, 0, 0, 0);
            INSERT INTO WatchRuns (Id, WatchId, CreatedTime, Status, Error)
            VALUES (1, 1, '2026-11-01 12:00:00', 2, 'Interrupted because the Backend restarted.'),
                   (2, 1, '2026-11-01 12:05:00', 2, 'Error: No price');
            """,
            Ct);

        await migrator.MigrateAsync(cancellationToken: Ct);

        var watch = await dbContext.Watches.SingleAsync(Ct);
        Assert.AreEqual(3, watch.KnownFailureThreshold);
        var runs = await dbContext.WatchRuns.OrderBy(r => r.Id).Select(r => r.Interrupted).ToListAsync(Ct);
        Assert.AreSequenceEqual([true, false], runs.ToArray());
    }
}
