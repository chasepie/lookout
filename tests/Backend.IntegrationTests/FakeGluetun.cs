using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.VpnServices;
using ChasePie.Lookout.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>
/// gluetun's control server, for the requests <see cref="GluetunHttpClient"/> makes, whose tunnel comes up as soon as it
/// switches.
/// </summary>
/// <param name="regions">The regions gluetun is restricted to; <see langword="null"/> for any region.</param>
internal sealed class FakeGluetun(string[]? regions)
{
    public const string Pia = "private internet access";

    private volatile string[]? _regions = regions;

    /// <summary>
    /// The regions gluetun was switched to, in order.
    /// </summary>
    public ConcurrentQueue<string> SwitchedTo { get; } = new();

    /// <summary>
    /// Whether gluetun refuses switches, as when its control server is down.
    /// </summary>
    public bool RefuseSwitches { get; set; }

    /// <summary>
    /// When set, switches wait for it to complete before gluetun answers.
    /// </summary>
    public TaskCompletionSource? HoldSwitches { get; set; }

    /// <summary>
    /// Completes when a switch reaches gluetun, though it may be held by <see cref="HoldSwitches"/>.
    /// </summary>
    public TaskCompletionSource SwitchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The settings that point the Backend at this gluetun, limited to <paramref name="serverRegions"/>, with its servers
    /// list, which offers <paramref name="servers"/>, in <paramref name="dataDirectory"/>.
    /// </summary>
    public static Dictionary<string, string?> Settings(
        string dataDirectory, string serverRegions, params string[] servers)
    {
        var gluetunDataDirectory = Path.Join(dataDirectory, "gluetun");
        WriteServers(gluetunDataDirectory, servers);
        return new Dictionary<string, string?>
        {
            [EnvVars.GluetunUrl] = "http://gluetun-control.test:8000",
            [EnvVars.GluetunApiKey] = "key",
            [EnvVars.GluetunProxyUrl] = "http://lookout@gluetun-proxy.test:8888",
            [EnvVars.GluetunProxyPassword] = "secret",
            [EnvVars.GluetunDataDir] = gluetunDataDirectory,
            [EnvVars.GluetunServerRegions] = serverRegions,
        };
    }

    /// <summary>
    /// Sends the Backend's gluetun requests here, and has its probe find the tunnel up at once.
    /// </summary>
    public void Register(IServiceCollection services)
    {
        services.AddHttpClient<GluetunHttpClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(HandleAsync));
        services.AddHttpClient<VpnProbeHttpClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ =>
                Task.FromResult(Text(HttpStatusCode.OK, "ip=203.0.113.7\n"))));
    }

    private static void WriteServers(string dataDirectory, string[] regions)
    {
        Directory.CreateDirectory(dataDirectory);
        var servers = new JsonObject
        {
            [Pia] = new JsonObject
            {
                ["servers"] = new JsonArray([.. regions.Select(region => new JsonObject
                {
                    ["vpn"] = "openvpn",
                    ["region"] = region,
                })]),
            },
        };
        File.WriteAllText(Path.Join(dataDirectory, "servers.json"), servers.ToJsonString());
    }

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
    {
        if (request.RequestUri?.AbsolutePath != "/v1/vpn/settings")
        {
            return Text(HttpStatusCode.NotFound, "not found");
        }

        if (request.Method == HttpMethod.Put)
        {
            SwitchStarted.TrySetResult();
            if (HoldSwitches is { } hold)
            {
                await hold.Task;
            }

            if (RefuseSwitches)
            {
                return Text(HttpStatusCode.InternalServerError, "gluetun is unwell");
            }

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
            var region = body!["provider"]!["server_selection"]!["regions"]![0]!.GetValue<string>();
            SwitchedTo.Enqueue(region);
            _regions = [region];
            return Text(HttpStatusCode.OK, "settings updated");
        }

        var settings = new JsonObject
        {
            ["type"] = "openvpn",
            ["provider"] = new JsonObject
            {
                ["name"] = Pia,
                ["server_selection"] = new JsonObject
                {
                    ["regions"] = _regions is { } current ? new JsonArray([.. current.Select(r => JsonValue.Create(r))]) : null,
                },
            },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    public static HttpResponseMessage Text(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "text/plain") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
}
