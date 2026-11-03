using ChasePie.Lookout.Core;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Database;

public class LookoutDbContext : DbContext
{
    public DbSet<Watch> Watches { get; set; } = null!;
    public DbSet<WatchRun> WatchRuns { get; set; } = null!;

    private readonly string _dbPath;

    public LookoutDbContext()
    {
        var folder = Environment.SpecialFolder.LocalApplicationData;
        var path = Environment.GetFolderPath(folder);
        _dbPath = Path.Join(path, "lookout.db");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseSqlite($"Data Source={_dbPath}");
}
