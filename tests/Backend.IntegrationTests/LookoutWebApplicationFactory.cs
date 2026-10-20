using ChasePie.Lookout.Backend.Runner;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Boots the backend in-process against a temporary SQLite database.
/// Every host it builds, including those from <c>WithWebHostBuilder</c>, gets its own database,
/// and all of them are deleted when the factory is disposed.
/// Time stands still until a test advances <see cref="Time"/>.
/// </summary>
public sealed class LookoutWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly List<string> _databasePaths = [];
    public FakeRunnerClient Runner { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    /// <summary>Uses the real clock instead of <see cref="Time"/>, for a host that a browser uses interactively.</summary>
    public bool UseSystemTime { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.Combine(RepositoryRoot.Find(), "src/Backend"));
        var databasePath = Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");
        _databasePaths.Add(databasePath);

        builder.UseEnvironment("Testing");
        builder.UseSetting("Logging:LogLevel:Default", "Error");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IRunnerClient>(Runner);
            services.AddSingleton(UseSystemTime ? TimeProvider.System : Time);
        });
        builder.UseSetting("ConnectionStrings:Lookout", $"Data Source={databasePath};Pooling=False");
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _databasePaths.ForEach(File.Delete);
            GC.SuppressFinalize(this);
        }
    }
}
