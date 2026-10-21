using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class DataDirectoryTests(TestContext testContext)
{
    [TestMethod]
    public async Task KeepsARelativeDatabasePathInTheDataDirectory()
    {
        // Not created beforehand, as a fresh container volume's subdirectory wouldn't be
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"lookout-data-{Guid.NewGuid():N}");
        try
        {
            await StartAsync(builder => builder.UseSetting("Storage:DataDirectory", dataDirectory));

            Assert.IsTrue(File.Exists(Path.Combine(dataDirectory, "lookout.db")), "Expected the database in the data directory");
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task KeepsARelativeDatabasePathInTheContentRootByDefault()
    {
        var contentRoot = Directory.CreateTempSubdirectory("lookout-content-").FullName;
        try
        {
            await StartAsync(builder => builder.UseContentRoot(contentRoot));

            Assert.IsTrue(File.Exists(Path.Combine(contentRoot, "lookout.db")), "Expected the database in the content root");
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    /// <summary>Starts the backend with a relative database path, as the default configuration has, and checks it's healthy.</summary>
    private async Task StartAsync(Action<IWebHostBuilder> configure)
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Lookout", "Data Source=lookout.db;Pooling=False");
            configure(builder);
        });
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
