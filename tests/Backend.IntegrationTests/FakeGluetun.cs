using System.Collections.Concurrent;
using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// gluetun's control server, as far as Lookout uses it. Like gluetun with Lookout's role, it answers only requests
/// carrying that role's key in X-API-Key, and refuses others with 401.
/// </summary>
public sealed class FakeGluetun : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeGluetun(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    public Uri Address { get; }
    public string VpnStatus { get; set; } = "running";
    public string PublicIp { get; set; } =
        """{"public_ip":"198.51.100.23","region":"Florida","country":"United States","city":"Miami","timezone":"America/New_York"}""";
    /// <summary>Whether gluetun closes the connection partway through its public IP's response.</summary>
    public bool DropsPublicIpResponse { get; set; }
    public TimeSpan VpnStatusDelay { get; set; }
    public TimeSpan PublicIpDelay { get; set; }
    /// <summary>Each authenticated change requested, as its method, path and body.</summary>
    public ConcurrentQueue<string> Changes { get; } = new();

    public GluetunVpnRotator CreateRotator(string apiKey) =>
        new(new HttpClient { BaseAddress = Address }, new GluetunSettings(apiKey, new ProxySettings("http://gluetun:8888")),
            TimeProvider.System);

    public static async Task<FakeGluetun> StartAsync(string apiKey)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeGluetun? gluetun = null;
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers["X-API-Key"] != apiKey)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        app.MapGet("/v1/vpn/status", async (HttpContext context) =>
        {
            await Task.Delay(gluetun!.VpnStatusDelay, context.RequestAborted);
            return Results.Text($$"""{"status":"{{gluetun.VpnStatus}}"}""", "application/json");
        });
        app.MapPut("/v1/vpn/status", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            gluetun!.Changes.Enqueue($"PUT /v1/vpn/status {await reader.ReadToEndAsync()}");
            return Results.Text("""{"outcome":"stopped"}""", "application/json");
        });
        app.MapGet("/v1/publicip/ip", async (HttpContext context) =>
        {
            await Task.Delay(gluetun!.PublicIpDelay, context.RequestAborted);
            context.Response.ContentType = "application/json";
            if (gluetun.DropsPublicIpResponse)
            {
                context.Response.ContentLength = 1000;
                await context.Response.WriteAsync("""{"public_ip":""", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                context.Abort();
                return;
            }
            await context.Response.WriteAsync(gluetun.PublicIp, context.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        gluetun = new FakeGluetun(app, new Uri(address));
        return gluetun;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
