using ChasePie.Lookout.Model;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Database;

/// <summary>Lookout's SQLite database.</summary>
public class LookoutDbContext(DbContextOptions<LookoutDbContext> options) : DbContext(options)
{
    public DbSet<Watch> Watches => Set<Watch>();
    public DbSet<ScriptVersion> ScriptVersions => Set<ScriptVersion>();
    public DbSet<Run> Runs => Set<Run>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Watch>()
            .HasMany(watch => watch.ScriptVersions)
            .WithOne()
            .HasForeignKey(version => version.WatchId);

        modelBuilder.Entity<Watch>()
            .HasOne(watch => watch.CurrentScriptVersion)
            .WithMany()
            .HasForeignKey(watch => watch.CurrentScriptVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Watch>()
            .HasMany(watch => watch.Runs)
            .WithOne()
            .HasForeignKey(run => run.WatchId);

        modelBuilder.Entity<Run>()
            .HasOne<ScriptVersion>()
            .WithMany()
            .HasForeignKey(run => run.ScriptVersionId)
            // Deleting a Watch cascades to its Script versions and Runs in an order SQLite chooses;
            // NO ACTION is checked once the whole delete finishes, whereas RESTRICT fails mid-cascade.
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Watch>()
            .Property(watch => watch.Revision)
            .IsConcurrencyToken();

        modelBuilder.Entity<Run>()
            .Property(run => run.Outcome)
            .HasConversion<string>();

        modelBuilder.Entity<Run>()
            .HasIndex(run => new { run.WatchId, run.Id });
    }
}
