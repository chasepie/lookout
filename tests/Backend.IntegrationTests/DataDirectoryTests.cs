using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class DataDirectoryTests(TestContext testContext)
{
    /// <summary>A relative database path, as the default configuration has.</summary>
    private const string RelativeDatabase = "Data Source=lookout.db;Pooling=False";

    [TestMethod]
    public async Task KeepsARelativeDatabasePathInTheDataDirectory()
    {
        // Not created beforehand, as a fresh container volume's subdirectory wouldn't be
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"lookout-data-{Guid.NewGuid():N}");
        try
        {
            await StartAsync(RelativeDatabase, builder => builder.UseSetting("Storage:DataDirectory", dataDirectory));

            Assert.IsTrue(File.Exists(Path.Combine(dataDirectory, "lookout.db")), "Expected the database in the data directory");
        }
        finally
        {
            DeleteDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task KeepsARelativeDatabasePathInTheContentRootByDefault()
    {
        var contentRoot = Directory.CreateTempSubdirectory("lookout-content-").FullName;
        try
        {
            await StartAsync(RelativeDatabase, builder => builder.UseContentRoot(contentRoot));

            Assert.IsTrue(File.Exists(Path.Combine(contentRoot, "lookout.db")), "Expected the database in the content root");
        }
        finally
        {
            DeleteDirectory(contentRoot);
        }
    }

    [TestMethod]
    public async Task KeepsADatabaseUriAsGiven()
    {
        var contentRoot = Directory.CreateTempSubdirectory("lookout-content-").FullName;
        var databasePath = Path.Combine(Path.GetTempPath(), $"lookout-uri-{Guid.NewGuid():N}.db");
        try
        {
            await StartAsync($"Data Source=file:{databasePath};Pooling=False", builder => builder.UseContentRoot(contentRoot));

            Assert.IsTrue(File.Exists(databasePath), "Expected the database at the URI's path");
        }
        finally
        {
            File.Delete(databasePath);
            DeleteDirectory(contentRoot);
        }
    }

    /// <summary>Starts the backend with the database <paramref name="connectionString"/> names, and checks it's healthy.</summary>
    private async Task StartAsync(string connectionString, Action<IWebHostBuilder> configure)
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Lookout", connectionString);
            configure(builder);
        });
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Deletes a test's directory, which a backend that failed to start may not have created.</summary>
    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
