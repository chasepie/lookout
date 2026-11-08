using ChasePie.Lookout.Backend.Auth;
using ChasePie.Lookout.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ChasePie.Lookout.Backend.Database;

public class LookoutDbContext(DbContextOptions<LookoutDbContext> options) : DbContext(options)
{
    private const string DatabaseFileName = "lookout.db";

    public DbSet<Watch> Watches { get; set; } = null!;
    public DbSet<WatchRun> WatchRuns { get; set; } = null!;
    public DbSet<VpnSettings> VpnSettings { get; set; } = null!;
    public DbSet<OwnerSessionState> OwnerSessionStates { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The single settings row always exists, so nothing has to create it on first use
        modelBuilder.Entity<VpnSettings>().HasData(new VpnSettings { Id = Core.VpnSettings.SingletonId });
        modelBuilder.Entity<OwnerSessionState>().HasData(new OwnerSessionState { Id = OwnerSessionState.SingletonId });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every DateTime is stored in UTC. SQLite keeps no DateTimeKind, so without this the values read back as
        // Unspecified, serialize without a "Z", and the frontend shows them as local time.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>
    /// The connection string for the database file in the <c>LOOKOUT_BACKEND_DATABASE_DIR</c> directory,
    /// which is created if it doesn't exist yet.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>LOOKOUT_BACKEND_DATABASE_DIR</c> is not set.</exception>
    public static string GetConnectionString(IConfiguration configuration)
    {
        var databaseDir = EnvironmentVariables.GetRequiredDirectory(
            configuration, EnvironmentVariables.DatabaseDir);
        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.Join(databaseDir, DatabaseFileName),
        }.ToString();
    }
}
