using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Runner.IntegrationTests;

/// <summary>
/// Runs the Runner in memory with its own scripts directory inside <see cref="DataDirectory"/>, deleted on disposal.
/// </summary>
internal sealed class RunnerFactory : WebApplicationFactory<Program>
{
    public static readonly string ApiKey = new('k', RunnerApiKey.MinLength);

    /// <summary>
    /// The route pattern of the endpoint a request reached, on every response; absent if it reached none.
    /// </summary>
    public const string MatchedRouteHeader = "X-Test-Matched-Route";

    /// <summary>
    /// Where the Runner's browserless is, unless a test says otherwise. Nothing listens there, so a script that gets as
    /// far as connecting fails.
    /// </summary>
    public const string BrowserlessUrl = "http://127.0.0.1:9";

    private readonly string _environment;
    private readonly string? _browserlessUrl;

    /// <param name="browserlessUrl">The Runner's <c>LOOKOUT_BROWSERLESS_URL</c>, or <see langword="null"/> for none.</param>
    public RunnerFactory(string environment = "Production", string? browserlessUrl = BrowserlessUrl)
    {
        _environment = environment;
        _browserlessUrl = browserlessUrl;
        DataDirectory = Directory.CreateTempSubdirectory("lookout-runner-tests-").FullName;
        ScriptsDirectory = Path.Join(DataDirectory, "scripts");
    }

    /// <summary>
    /// A directory that holds <see cref="ScriptsDirectory"/>, for files scripts must not be able to read.
    /// </summary>
    public string DataDirectory { get; }

    public string ScriptsDirectory { get; }

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
        builder.UseSetting(LookoutEnvironmentVariables.WatchScriptsDir, ScriptsDirectory);
        builder.UseSetting(LookoutEnvironmentVariables.BrowserlessUrl, _browserlessUrl);
        builder.UseSetting(LookoutEnvironmentVariables.RunnerApiKey, ApiKey);
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, MatchedRouteStartupFilter>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Adds <see cref="MatchedRouteHeader"/> to every response, so tests can tell which endpoint a request reached.
    /// </summary>
    private sealed class MatchedRouteStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Response.OnStarting(() =>
                {
                    if (context.GetEndpoint() is RouteEndpoint endpoint)
                    {
                        context.Response.Headers[MatchedRouteHeader] = endpoint.RoutePattern.RawText;
                    }

                    return Task.CompletedTask;
                });
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
