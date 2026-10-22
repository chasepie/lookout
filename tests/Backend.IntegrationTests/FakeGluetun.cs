using System.Collections.Concurrent;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;
using Microsoft.AspNetCore.Http;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// gluetun's control server, as far as Lookout uses it. Like gluetun with Lookout's role, it answers only requests
/// carrying that role's key in X-API-Key, and refuses others with 401.
/// </summary>
public sealed class FakeGluetun : IAsyncDisposable
{
    private readonly FakeHttpServer _server;
    private readonly string _apiKey;
    private int _failingStarts;

    private FakeGluetun(FakeHttpServer server, string apiKey)
    {
        _server = server;
        _apiKey = apiKey;
    }

    public Uri Address => _server.BaseAddress;
    public string VpnStatus { get; set; } = "running";
    public string PublicIp { get; set; } =
        """{"public_ip":"198.51.100.23","region":"Florida","country":"United States","city":"Miami","timezone":"America/New_York"}""";
    /// <summary>Whether gluetun closes the connection partway through its public IP's response.</summary>
    public bool DropsPublicIpResponse { get; set; }
    public TimeSpan VpnStatusDelay { get; set; }
    public TimeSpan PublicIpDelay { get; set; }
    /// <summary>How many of the next requests to start the VPN fail with HTTP 500.</summary>
    public int FailingStarts
    {
        get => Volatile.Read(ref _failingStarts);
        set => Volatile.Write(ref _failingStarts, value);
    }
    /// <summary>Each authenticated change requested, as its method, path and body.</summary>
    public ConcurrentQueue<string> Changes { get; } = new();

    /// <summary>
    /// A rotator for gluetun with Lookout's key, or another, whose checks through the proxy go to
    /// <paramref name="runner"/>, or fail without one.
    /// </summary>
    public GluetunVpnRotator CreateRotator(string apiKey, TimeProvider? timeProvider = null, IRunnerClient? runner = null,
        TimeSpan? statusTimeout = null)
    {
        timeProvider ??= TimeProvider.System;
        runner ??= new FakeRunnerClient();
        var settings = new GluetunSettings(apiKey, new ProxySettings("http://gluetun:8888"));
        return new(new HttpClient { BaseAddress = Address }, statusTimeout is { } timeout ? settings with { StatusTimeout = timeout } : settings,
            new ProxiedExitIp(() => runner, timeProvider), timeProvider);
    }

    public static async Task<FakeGluetun> StartAsync(string apiKey)
    {
        var server = await FakeHttpServer.StartAsync(CancellationToken.None);
        var gluetun = new FakeGluetun(server, apiKey);
        server.Respond = gluetun.RespondAsync;
        return gluetun;
    }

    private async Task RespondAsync(HttpContext context)
    {
        if (context.Request.Headers["X-API-Key"] != _apiKey)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        context.Response.ContentType = "application/json";
        switch (context.Request.Method, context.Request.Path.Value)
        {
            case ("GET", "/v1/vpn/status"):
                await Task.Delay(VpnStatusDelay, context.RequestAborted);
                await context.Response.WriteAsync($$"""{"status":"{{VpnStatus}}"}""", context.RequestAborted);
                break;
            case ("PUT", "/v1/vpn/status"):
                var change = $"PUT /v1/vpn/status {FakeHttpServer.RequestOf(context).Body}";
                Changes.Enqueue(change);
                if (change.Contains("running", StringComparison.Ordinal) && Interlocked.Decrement(ref _failingStarts) >= 0)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return;
                }
                await context.Response.WriteAsync("""{"outcome":"stopped"}""", context.RequestAborted);
                break;
            case ("PUT", "/v1/updater/status"):
                Changes.Enqueue($"PUT /v1/updater/status {FakeHttpServer.RequestOf(context).Body}");
                await context.Response.WriteAsync("""{"outcome":"running"}""", context.RequestAborted);
                break;
            case ("GET", "/v1/publicip/ip"):
                await Task.Delay(PublicIpDelay, context.RequestAborted);
                if (DropsPublicIpResponse)
                {
                    context.Response.ContentLength = 1000;
                    await context.Response.WriteAsync("""{"public_ip":""", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    context.Abort();
                    return;
                }
                await context.Response.WriteAsync(PublicIp, context.RequestAborted);
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}
