using ChasePie.Lookout.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Database;

public class LookoutDbContext(DbContextOptions<LookoutDbContext> options) : DbContext(options)
{
    private const string DatabaseFileName = "lookout.db";

    public DbSet<Watch> Watches { get; set; } = null!;
    public DbSet<WatchRun> WatchRuns { get; set; } = null!;

    /// <summary>
    /// The connection string for the database file in the <c>LOOKOUT_DATABASE_DIR</c> directory,
    /// which is created if it doesn't exist yet.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>LOOKOUT_DATABASE_DIR</c> is not set.</exception>
    public static string GetConnectionString(IConfiguration configuration)
    {
        var databaseDir = LookoutEnvironmentVariables.GetRequiredDirectory(
            configuration, LookoutEnvironmentVariables.DatabaseDir);
        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.Join(databaseDir, DatabaseFileName),
        }.ToString();
    }
}
