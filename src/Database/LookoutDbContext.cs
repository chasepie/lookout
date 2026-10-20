using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Database;

/// <summary>Lookout's SQLite database.</summary>
public class LookoutDbContext(DbContextOptions<LookoutDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Session> Sessions => Set<Session>();
    /// <summary>The keys that protect session cookies, kept here so logins survive a restart.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Watch> Watches => Set<Watch>();
    public DbSet<ScriptVersion> ScriptVersions => Set<ScriptVersion>();
    public DbSet<Run> Runs => Set<Run>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(account =>
        {
            account.Property(item => item.Id).ValueGeneratedNever();
            account.ToTable(table => table.HasCheckConstraint("CK_Accounts_Singleton", $"Id = {Account.SingletonId}"));
            account.HasMany<Session>().WithOne().HasForeignKey(session => session.AccountId);
        });

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
