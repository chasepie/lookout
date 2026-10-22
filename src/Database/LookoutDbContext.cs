using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<WatchChannel> WatchChannels => Set<WatchChannel>();
    public DbSet<RunDebugOutput> RunDebugOutputs => Set<RunDebugOutput>();
    public DbSet<Settings> Settings => Set<Settings>();

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

        modelBuilder.Entity<Watch>()
            .HasMany(watch => watch.Channels)
            .WithMany(channel => channel.Watches)
            .UsingEntity<WatchChannel>(
                choice => choice.HasOne<Channel>().WithMany().HasForeignKey(item => item.ChannelId),
                choice => choice.HasOne<Watch>().WithMany().HasForeignKey(item => item.WatchId),
                choice => choice.HasKey(item => new { item.WatchId, item.ChannelId }));

        modelBuilder.Entity<Channel>()
            .Property(channel => channel.Kind)
            .HasConversion<string>();

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

        modelBuilder.Entity<Watch>()
            .ComplexProperty(watch => watch.Schedule, schedule =>
            {
                schedule.Property(value => value.Kind).HasConversion<string>();
                schedule.Property(value => value.Unit).HasConversion<string>();
            });

        modelBuilder.Entity<Watch>()
            .Property(watch => watch.NextRunAt)
            // SQLite can't compare DateTimeOffset text, and the scheduler queries by it; Lookout stores UTC throughout.
            .HasConversion(new DateTimeOffsetToBinaryConverter());

        modelBuilder.Entity<Watch>()
            .Property(watch => watch.LastScheduledRunAt)
            // Schedule changes compare it to detect a Run that started meanwhile.
            .HasConversion(new DateTimeOffsetToBinaryConverter());

        modelBuilder.Entity<Watch>()
            .HasIndex(watch => watch.NextRunAt);

        modelBuilder.Entity<Run>()
            .HasIndex(run => new { run.WatchId, run.Id });

        modelBuilder.Entity<ScriptVersion>()
            .HasIndex(version => new { version.WatchId, version.Number })
            .IsUnique();

        modelBuilder.Entity<Run>()
            // Retention prunes by it.
            .HasIndex(run => run.FinishedAt);

        modelBuilder.Entity<RunDebugOutput>(debugOutput =>
        {
            debugOutput.HasKey(item => item.RunId);
            debugOutput.HasOne(item => item.Run).WithOne().HasForeignKey<RunDebugOutput>(item => item.RunId);
        });

        modelBuilder.Entity<Settings>(settings =>
        {
            settings.Property(item => item.Id).ValueGeneratedNever();
            // The DebugOutputAndRetention migration inserts the only row, rather than seed data here, which later
            // migrations would reset to the current defaults, overwriting the user's settings.
            settings.ToTable(table => table.HasCheckConstraint("CK_Settings_Singleton", $"Id = {Lookout.Model.Settings.SingletonId}"));
        });
    }
}
