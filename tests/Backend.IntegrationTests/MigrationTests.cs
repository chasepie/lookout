using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class MigrationTests(TestContext testContext)
{
    [TestMethod]
    public async Task WatchesCreatedBeforeStatusesGetTheirFailureStreakAndTheDefaultTimeout()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            await using (var database = new LookoutDbContext(new DbContextOptionsBuilder<LookoutDbContext>().UseSqlite(connectionString).Options))
            {
                await database.GetService<IMigrator>().MigrateAsync("CreateWatches", testContext.CancellationToken);
                await InsertWatch(database, "Recovered", "Errored", "Succeeded");
                await InsertWatch(database, "Failing", "Succeeded", "KnownFailure", "Errored");
                await InsertWatch(database, "Never succeeded", "Errored");
            }
            await using var factory = new LookoutWebApplicationFactory();
            using var client = await factory
                .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Lookout", connectionString))
                .CreateSignedInClientAsync(testContext.CancellationToken);

            var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);

            var byName = watches!.ToDictionary(watch => watch!["name"]!.GetValue<string>(), watch => watch!);
            Assert.AreEqual(2, byName["Failing"]["failureStreak"]!.GetValue<int>());
            Assert.AreEqual("failing", byName["Failing"]["status"]!.GetValue<string>());
            Assert.AreEqual(1, byName["Never succeeded"]["failureStreak"]!.GetValue<int>());
            Assert.AreEqual(0, byName["Recovered"]["failureStreak"]!.GetValue<int>());
            Assert.AreEqual("healthy", byName["Recovered"]["status"]!.GetValue<string>());
            Assert.IsTrue(watches!.All(watch => watch!["timeoutSeconds"]!.GetValue<int>() == 120));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private async Task InsertWatch(LookoutDbContext database, string name, params string[] outcomes)
    {
        // EF Core stores Guids as upper-case text in SQLite.
        var watchId = Guid.NewGuid().ToString().ToUpperInvariant();
        var versionId = Guid.NewGuid().ToString().ToUpperInvariant();
        var baseline = outcomes.Contains("Succeeded") ? "£20" : null;
        var cancellationToken = testContext.CancellationToken;
        await database.Database.ExecuteSqlAsync(
            $"INSERT INTO Watches (Id, Name, NotificationUrl, Baseline, Revision) VALUES ({watchId}, {name}, 'https://example.com', {baseline}, {outcomes.Length})",
            cancellationToken);
        await database.Database.ExecuteSqlAsync(
            $"INSERT INTO ScriptVersions (Id, WatchId, Source, FileKind) VALUES ({versionId}, {watchId}, 'export default {{}}', 'ts')",
            cancellationToken);
        await database.Database.ExecuteSqlAsync(
            $"UPDATE Watches SET CurrentScriptVersionId = {versionId} WHERE Id = {watchId}", cancellationToken);
        foreach (var outcome in outcomes)
        {
            await database.Database.ExecuteSqlAsync(
                $"INSERT INTO Runs (WatchId, ScriptVersionId, Outcome, DurationMs, StartedAt, FinishedAt, IsChange) VALUES ({watchId}, {versionId}, {outcome}, 1, '2026-10-19 12:00:00+00:00', '2026-10-19 12:00:01+00:00', 0)",
                cancellationToken);
        }
    }
}
