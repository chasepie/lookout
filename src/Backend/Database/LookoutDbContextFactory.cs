using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChasePie.Lookout.Backend.Database;

/// <summary>
/// Creates the context for <c>dotnet ef</c>, which otherwise starts the Backend and needs its environment
/// variables. Adding a migration never opens the database, so the file name here is never used.
/// </summary>
public class LookoutDbContextFactory : IDesignTimeDbContextFactory<LookoutDbContext>
{
    public LookoutDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LookoutDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
