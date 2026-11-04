using ChasePie.Lookout.Backend.Database;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.UnitTests;

[TestClass]
public sealed class DevelopmentDataSeederTests : IDisposable
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "lookout-seeder-" + Guid.NewGuid());
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private string ExamplesDir => Path.Combine(_rootDir, "examples");
    private string ScriptsDir => Path.Combine(_rootDir, "scripts");

    [TestInitialize]
    public void Initialize()
    {
        Directory.CreateDirectory(ExamplesDir);
        Directory.CreateDirectory(ScriptsDir);
        _connection.Open();
        using var dbContext = CreateDbContext();
        dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_rootDir, recursive: true);
    }

    [TestMethod]
    public async Task CreatesAWatchForEachExampleScript()
    {
        await File.WriteAllTextAsync(Path.Combine(ExamplesDir, "b-example.ts"), "export default 1;", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(ExamplesDir, "a-example.ts"), "export default 2;", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(ExamplesDir, "package.json"), "{}", TestContext.CancellationToken);

        await SeedAsync(ExamplesDir);

        using var dbContext = CreateDbContext();
        var watches = await dbContext.Watches.OrderBy(w => w.Id).ToListAsync(TestContext.CancellationToken);
        Assert.AreSequenceEqual(["a-example", "b-example"], watches.Select(w => w.Name).ToArray());
        foreach (var watch in watches)
        {
            Assert.IsFalse(watch.ScheduleEnabled);
            Assert.IsFalse(watch.UseVpn);
            var storedPath = Path.Combine(ScriptsDir, WatchScriptsDirectory.GetStoredFileName(watch)!);
            Assert.AreEqual(
                await File.ReadAllTextAsync(Path.Combine(ExamplesDir, watch.ScriptFileName!), TestContext.CancellationToken),
                await File.ReadAllTextAsync(storedPath, TestContext.CancellationToken));
        }
    }

    [TestMethod]
    public async Task SkipsInvalidScriptsAndSeedsTheRest()
    {
        await File.WriteAllTextAsync(Path.Combine(ExamplesDir, "empty.ts"), "  ", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(ExamplesDir, "valid.ts"), "export default 1;", TestContext.CancellationToken);

        await SeedAsync(ExamplesDir);

        using var dbContext = CreateDbContext();
        var names = await dbContext.Watches.Select(w => w.Name).ToListAsync(TestContext.CancellationToken);
        Assert.AreSequenceEqual(["valid"], names);
    }

    [TestMethod]
    public async Task DoesNothingWhenWatchesExist()
    {
        await File.WriteAllTextAsync(Path.Combine(ExamplesDir, "example.ts"), "export default 1;", TestContext.CancellationToken);
        using (var dbContext = CreateDbContext())
        {
            dbContext.Watches.Add(new Watch { Name = "existing" });
            await dbContext.SaveChangesAsync(TestContext.CancellationToken);
        }

        await SeedAsync(ExamplesDir);

        using var verifyContext = CreateDbContext();
        var names = await verifyContext.Watches.Select(w => w.Name).ToListAsync(TestContext.CancellationToken);
        Assert.AreSequenceEqual(["existing"], names);
    }

    [TestMethod]
    public async Task DoesNothingWhenTheExamplesDirectoryDoesNotExist()
    {
        await SeedAsync(Path.Combine(_rootDir, "does-not-exist"));

        using var dbContext = CreateDbContext();
        Assert.IsFalse(await dbContext.Watches.AnyAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public void FindsTheExamplesDirectoryAtTheRepoRoot()
    {
        File.WriteAllText(Path.Combine(_rootDir, RepoRoot.SolutionFileName), "");
        var projectDir = Directory.CreateDirectory(Path.Combine(_rootDir, "src", "Backend")).FullName;

        Assert.AreEqual(ExamplesDir, DevelopmentDataSeeder.FindExamplesDirectory(projectDir));
    }

    [TestMethod]
    public void FindsNoExamplesDirectoryWhenTheRepoHasNone()
    {
        File.WriteAllText(Path.Combine(_rootDir, RepoRoot.SolutionFileName), "");
        Directory.Delete(ExamplesDir);

        Assert.IsNull(DevelopmentDataSeeder.FindExamplesDirectory(_rootDir));
    }

    public TestContext TestContext { get; set; } = null!;

    private LookoutDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<LookoutDbContext>().UseSqlite(_connection).Options);

    private async Task SeedAsync(string examplesDir)
    {
        using var dbContext = CreateDbContext();
        var scriptManager = new WatchScriptManager(dbContext, new WatchScriptsDirectory(ScriptsDir));
        var seeder = new DevelopmentDataSeeder(dbContext, scriptManager, NullLogger<DevelopmentDataSeeder>.Instance);
        await seeder.SeedAsync(examplesDir, TestContext.CancellationToken);
    }
}
