using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChasePie.Lookout.Backend.Tests;

/// <summary>
/// Boots the backend in-process against its own temporary SQLite database,
/// which is deleted when the factory is disposed.
/// </summary>
public sealed class LookoutWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Lookout", $"Data Source={_databasePath};Pooling=False");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        File.Delete(_databasePath);
        GC.SuppressFinalize(this);
    }
}
