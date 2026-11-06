using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Runner.IntegrationTests;

/// <summary>
/// Runs the Runner in memory with its own scripts directory, deleted on disposal.
/// </summary>
internal sealed class RunnerFactory : WebApplicationFactory<global::Runner.Program>
{
    public static readonly string ApiKey = new('k', RunnerApiKey.MinLength);

    private readonly string _environment;
    private readonly string _scriptsDirectory = Directory.CreateTempSubdirectory("lookout-runner-tests-").FullName;

    public RunnerFactory(string environment = "Production")
    {
        _environment = environment;
    }

    /// <summary>
    /// A client over HTTPS, sending <paramref name="apiKey"/> in the key's header unless it's <see langword="null"/>.
    /// </summary>
    public HttpClient CreateClient(string? apiKey)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add(RunnerApiKey.HeaderName, apiKey);
        }

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting(LookoutEnvironmentVariables.WatchScriptsDir, _scriptsDirectory);
        builder.UseSetting(LookoutEnvironmentVariables.RunnerApiKey, ApiKey);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_scriptsDirectory))
        {
            Directory.Delete(_scriptsDirectory, recursive: true);
        }
    }
}
