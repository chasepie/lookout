using ChasePie.Lookout.Backend.Scheduling;
using ChasePie.Lookout.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Startup;

/// <summary>Lookout's SQLite database: where it lives, migrating it at startup, and checking it's healthy.</summary>
public static class LookoutDatabase
{
    public static void AddLookoutDatabase(
        this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var connectionString = ConnectionString(configuration, environment);
        services.AddDbContext<LookoutDbContext>(options => options.UseSqlite(connectionString));
        services.AddHealthChecks()
            .AddDbContextCheck<LookoutDbContext>(customTestQuery: async (db, cancellationToken) =>
            {
                // CanConnect only checks that the SQLite file exists, and SQLite recreates a missing file empty;
                // reading the migrations history checks it's the database startup migrated.
                await db.Database.ExecuteSqlRawAsync("SELECT COUNT(*) FROM __EFMigrationsHistory", cancellationToken);
                return true;
            });
    }

    /// <summary>Migrates the database, seeding a new development database with <see cref="ExampleWatches"/>.</summary>
    public static void Migrate(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LookoutDbContext>();
        // Seeding only a database this startup creates keeps a removed example from coming back.
        var created = !database.Database.GetAppliedMigrations().Any();
        database.Database.Migrate();
        if (created && app.Environment.IsDevelopment())
        {
            ExampleWatches.Seed(database, scope.ServiceProvider.GetRequiredService<WatchSchedules>());
        }
    }

    /// <summary>
    /// The database's connection string, with a relative path resolved against the data directory
    /// (<c>Storage:DataDirectory</c>, by default the content root), which is created if it's missing. SQLite would
    /// resolve it against the working directory, which depends on how the app was started; in a container, that's
    /// lost with the container unless a volume happens to be mounted there. A URI, an in-memory database, or a path
    /// using Microsoft.Data.Sqlite's <c>|DataDirectory|</c> is left as given.
    /// </summary>
    private static string ConnectionString(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var connection = new SqliteConnectionStringBuilder(configuration.GetConnectionString("Lookout")
            ?? throw new InvalidOperationException("ConnectionStrings:Lookout is not configured"));
        if (connection.DataSource is "" or ":memory:"
            || connection.Mode == SqliteOpenMode.Memory
            || connection.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || connection.DataSource.StartsWith("|DataDirectory|", StringComparison.OrdinalIgnoreCase))
        {
            return connection.ConnectionString;
        }
        var dataDirectory = Path.GetFullPath(configuration["Storage:DataDirectory"] ?? ".", environment.ContentRootPath);
        connection.DataSource = Path.GetFullPath(connection.DataSource, dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(connection.DataSource)!);
        return connection.ConnectionString;
    }
}
