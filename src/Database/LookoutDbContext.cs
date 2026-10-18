using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Database;

/// <summary>Lookout's SQLite database.</summary>
public class LookoutDbContext(DbContextOptions<LookoutDbContext> options) : DbContext(options)
{
}
