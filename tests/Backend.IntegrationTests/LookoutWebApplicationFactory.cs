using ChasePie.Lookout.Backend.Runner;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Boots the backend in-process against a temporary SQLite database.
/// Every host it builds, including those from <c>WithWebHostBuilder</c>, gets its own database,
/// and all of them are deleted when the factory is disposed.
/// </summary>
public sealed class LookoutWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly List<string> _databasePaths = [];
    public FakeRunnerClient Runner { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Lookout.slnx")))
        {
            repository = repository.Parent;
        }
        builder.UseContentRoot(Path.Combine(repository?.FullName
            ?? throw new InvalidOperationException("Could not find the Lookout repository."), "src/Backend"));
        var databasePath = Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");
        _databasePaths.Add(databasePath);

        builder.UseEnvironment("Testing");
        builder.UseSetting("Logging:LogLevel:Default", "Error");
        builder.ConfigureTestServices(services => services.AddSingleton<IRunnerClient>(Runner));
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
