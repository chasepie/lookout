using System.Net;
using System.Net.Http.Json;
using ChasePie.Lookout.Backend.Auth;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// Runs the Backend in memory with its own database and scripts directories, deleted on disposal unless shared.
/// </summary>
internal sealed class LookoutFactory : WebApplicationFactory<Program>
{
    public const string Username = "owner";
    public const string Password = "correct horse battery";

    /// <summary>
    /// Requests with this header appear to come from the IP it holds, for testing per-client limits.
    /// </summary>
    public const string ClientIpHeader = "X-Test-Client-Ip";

    /// <summary>
    /// The route pattern of the endpoint a request reached, on every response; absent if it reached none.
    /// </summary>
    public const string MatchedRouteHeader = "X-Test-Matched-Route";

    private readonly string _environment;
    private readonly string _username;
    private readonly string _password;
    private readonly bool _ownsDataDirectory;
    private readonly IReadOnlyDictionary<string, string?> _settings;

    /// <param name="dataDirectory">
    /// A directory to keep the database and keys in, to share them with another factory as if the Backend restarted.
    /// </param>
    /// <param name="settings">More settings, as if from the environment; these win over the defaults above.</param>
    public LookoutFactory(
        string environment = "Production",
        string username = Username,
        string password = Password,
        string? dataDirectory = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        _environment = environment;
        _username = username;
        _password = password;
        _settings = settings ?? new Dictionary<string, string?>();
        _ownsDataDirectory = dataDirectory is null;
        DataDirectory = dataDirectory ?? Directory.CreateTempSubdirectory("lookout-tests-").FullName;
        WebRoot = CreateWebRoot(DataDirectory);
    }

    public string DataDirectory { get; }

    /// <summary>
    /// A stand-in for the built frontend, so serving it is tested whether or not this machine has a real build.
    /// </summary>
    public string WebRoot { get; }

    /// <summary>
    /// A content-hashed bundle in <see cref="WebRoot"/>, named like Angular's.
    /// </summary>
    public const string BundleFileName = "chunk-AB12cd34.js";

    /// <summary>
    /// Another bundle in <see cref="WebRoot"/>, whose hash has an underscore, as Angular's sometimes do.
    /// </summary>
    public const string UnderscoreBundleFileName = "chunk-BJ1G_bZo.js";

    public const string IndexHtml = "<!doctype html><title>Lookout</title>";

    private static string CreateWebRoot(string dataDirectory)
    {
        var webRoot = Path.Join(dataDirectory, "wwwroot");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Join(webRoot, "index.html"), IndexHtml);
        File.WriteAllText(Path.Join(webRoot, BundleFileName), "export {};");
        File.WriteAllText(Path.Join(webRoot, UnderscoreBundleFileName), "export {};");
        return webRoot;
    }

    /// <summary>
    /// A client that acts like a browser on Lookout's own pages: over HTTPS, keeping cookies, and sending the headers
    /// <see cref="CsrfProtection"/> requires.
    /// </summary>
    /// <param name="baseAddress">Where Lookout is served; HTTPS on localhost unless given.</param>
    /// <param name="handleCookies">Whether to keep cookies between requests, as a browser does.</param>
    public HttpClient CreateBrowserClient(Uri? baseAddress = null, bool handleCookies = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = baseAddress ?? new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = handleCookies,
        });
        AddBrowserHeaders(client);
        return client;
    }

    /// <summary>
    /// Makes <paramref name="client"/> send the headers a browser sends from Lookout's own pages, which
    /// <see cref="CsrfProtection"/> requires.
    /// </summary>
    public static void AddBrowserHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(CsrfProtection.HeaderName, "1");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
    }

    /// <summary>
    /// The session cookie a sign-in's response sets, as just "name=value", as a browser would send it back.
    /// </summary>
    public static string SessionCookie(HttpResponseMessage login) =>
        login.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(OwnerAuthentication.CookieName + "=", StringComparison.Ordinal))
            .Split(';')[0];

    /// <summary>
    /// Signs in with the Owner's credentials and returns the browser client holding the session.
    /// </summary>
    public async Task<HttpClient> CreateSignedInClientAsync(CancellationToken cancellationToken)
    {
        var client = CreateBrowserClient();
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { Username, Password }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting(WebHostDefaults.WebRootKey, WebRoot);
        builder.UseSetting(EnvironmentVariables.OwnerUsername, _username);
        builder.UseSetting(EnvironmentVariables.OwnerPassword, _password);
        builder.UseSetting(EnvironmentVariables.DatabaseDir, Path.Join(DataDirectory, "database"));
        builder.UseSetting(EnvironmentVariables.WatchScriptsDir, Path.Join(DataDirectory, "scripts"));
        // Nothing listens there; runs aren't exercised
        builder.UseSetting(EnvironmentVariables.RunnerUrl, "http://127.0.0.1:9");
        builder.UseSetting(EnvironmentVariables.RunnerApiKey, new string('k', RunnerApiKey.MinLength));
        foreach (var (name, value) in _settings)
        {
            builder.UseSetting(name, value);
        }

        builder.ConfigureServices(services => services
            .AddTransient<IStartupFilter, ClientIpStartupFilter>()
            .AddTransient<IStartupFilter, MatchedRouteStartupFilter>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsDataDirectory)
        {
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // SQLite can still hold the file briefly; it's a temp directory
            }
        }
    }

    private sealed class ClientIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(ClientIpHeader, out var ip))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                }

                return nextMiddleware(context);
            });
            next(app);
        };
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
