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

    public const string BrowserlessToken = "browserless-token";

    private readonly string _environment;
    private readonly string? _browserlessUrl;
    private readonly string? _browserlessToken;
    private readonly string? _cliRoot;

    /// <param name="browserlessUrl">The Runner's <c>BROWSERLESS_URL</c>, or <see langword="null"/> for none.</param>
    /// <param name="browserlessToken">The Runner's <c>BROWSERLESS_TOKEN</c>, or <see langword="null"/> for none.</param>
    /// <param name="standInCli">
    /// Whether the Runner runs <see cref="StandInCli"/> instead of the real watch CLI, so scripts are instructions for it.
    /// </param>
    public RunnerFactory(
        string environment = "Production",
        string? browserlessUrl = BrowserlessUrl,
        string? browserlessToken = BrowserlessToken,
        bool standInCli = false)
    {
        _environment = environment;
        _browserlessUrl = browserlessUrl;
        _browserlessToken = browserlessToken;
        DataDirectory = Directory.CreateTempSubdirectory("lookout-runner-tests-").FullName;
        ScriptsDirectory = Path.Join(DataDirectory, "scripts");
        if (standInCli)
        {
            _cliRoot = Path.Join(DataDirectory, "cli-root");
            var sdkDirectory = Path.Join(_cliRoot, "node_modules", "@chasepie", "lookout-watch-sdk", "dist");
            Directory.CreateDirectory(sdkDirectory);
            File.WriteAllText(Path.Join(sdkDirectory, "cli.js"), StandInCli);
        }
    }

    /// <summary>
    /// A watch CLI that does what its script, a JSON file, says, as <c>lookout-watch run &lt;script&gt; --output
    /// &lt;file&gt; [flags]</c> is run: writes <c>write</c> to the output file as JSON, or <c>writeRaw</c> as it is, or
    /// with <c>echoFlags</c> a success whose content is the flags it was given as a JSON array; prints <c>stderr</c>; and
    /// exits with <c>exitCode</c>.
    /// </summary>
    public const string StandInCli = """
        import { readFileSync, writeFileSync } from 'node:fs';

        const [, , , scriptPath, , outputPath, ...flags] = process.argv;
        const script = JSON.parse(readFileSync(scriptPath, 'utf8'));
        const output = script.echoFlags ? { success: true, content: JSON.stringify(flags) } : script.write;
        if (output !== undefined) {
          writeFileSync(outputPath, JSON.stringify(output));
        } else if (script.writeRaw !== undefined) {
          writeFileSync(outputPath, script.writeRaw);
        }
        if (script.stderr !== undefined) {
          console.error(script.stderr);
        }
        process.exitCode = script.exitCode ?? 0;
        """;

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
        builder.UseSetting(EnvVars.LookoutWatchScriptsDir, ScriptsDirectory);
        builder.UseSetting(EnvVars.BrowserlessUrl, _browserlessUrl);
        builder.UseSetting(EnvVars.BrowserlessToken, _browserlessToken);
        builder.UseSetting(EnvVars.LookoutRunnerApiKey, ApiKey);
        if (_cliRoot is not null)
        {
            builder.UseSetting(EnvVars.WatchCliRoot, _cliRoot);
        }

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
