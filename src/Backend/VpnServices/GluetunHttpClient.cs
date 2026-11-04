using System.Net.Http.Headers;
using System.Text.Json;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// Typed HTTP client for gluetun's control server (<c>LOOKOUT_GLUETUN_URL</c>).
/// </summary>
/// <remarks>
/// Switching regions uses <c>PUT /v1/vpn/settings</c>, which the gluetun wiki doesn't document. It merges the
/// body over the current VPN settings and, if they changed, restarts the tunnel: the public IP clears at once
/// and normally comes back when the new tunnel is up, while <c>/v1/vpn/status</c> stays <c>running</c> throughout.
/// gluetun looks the IP up with third-party services once per tunnel, and leaves it empty if they all fail.
/// Settings changed this way last until gluetun restarts.
/// </remarks>
public class GluetunHttpClient(HttpClient httpClient)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// The VPN settings gluetun is using: its provider, VPN type and region filter.
    /// </summary>
    public async Task<GluetunVpnSettings> GetVpnSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("v1/vpn/settings", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GluetunVpnSettings>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("gluetun returned empty VPN settings.");
    }

    /// <summary>
    /// Restricts gluetun's servers to <paramref name="region"/>, which restarts the tunnel on a server there.
    /// Returns once gluetun has started reconnecting; <see cref="VpnProxyProbe"/> tells when it's done.
    /// </summary>
    /// <exception cref="HttpRequestException">gluetun rejected the region or couldn't be reached.</exception>
    public async Task SetRegionAsync(string region, CancellationToken cancellationToken = default)
    {
        var body = new { Provider = new { ServerSelection = new { Regions = new[] { region } } } };
        using var response = await httpClient.PutAsJsonAsync("v1/vpn/settings", body, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// The tunnel's public IP and where it geolocates, or <see langword="null"/> while the tunnel is down,
    /// including while it reconnects after a region switch, or if gluetun's lookup failed.
    /// </summary>
    public async Task<GluetunPublicIp?> GetPublicIpAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("v1/publicip/ip", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var publicIp = await response.Content.ReadFromJsonAsync<GluetunPublicIp>(JsonOptions, cancellationToken);
        return string.IsNullOrEmpty(publicIp?.PublicIp) ? null : publicIp;
    }

    /// <summary>
    /// gluetun answers errors with a plain-text reason, such as the list of valid regions for a bad one.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        throw new HttpRequestException(
            $"gluetun returned {(int)response.StatusCode}: {error}", null, response.StatusCode);
    }

    /// <summary>
    /// Configures the client from <c>LOOKOUT_GLUETUN_URL</c> and <c>LOOKOUT_GLUETUN_API_KEY</c>.
    /// Without a URL the client has no base address; nothing calls it then, see <see cref="VpnConfiguration.IsEnabled"/>.
    /// </summary>
    internal static void Configure(IServiceProvider services, HttpClient client)
    {
        var configuration = services.GetRequiredService<VpnConfiguration>();
        client.BaseAddress = configuration.GluetunUrl;
        client.Timeout = RequestTimeout;
        if (configuration.ApiKey is { } apiKey)
        {
            client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        }
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}

/// <summary>
/// The parts of gluetun's <c>GET /v1/vpn/settings</c> response the Backend uses.
/// </summary>
public sealed record GluetunVpnSettings(string Type, GluetunProvider Provider);

public sealed record GluetunProvider(string Name, GluetunServerSelection ServerSelection);

/// <param name="Regions"><see langword="null"/> when gluetun may pick a server in any region.</param>
public sealed record GluetunServerSelection(IReadOnlyList<string>? Regions);

/// <summary>
/// gluetun's <c>GET /v1/publicip/ip</c> response. The location comes from an IP geolocation service, so its
/// region names differ from the VPN provider's: PIA's "Netherlands" region shows as "Noord-Holland".
/// </summary>
public sealed record GluetunPublicIp(
    string PublicIp,
    string? Country,
    string? Region,
    string? City,
    string? Organization);

/// <summary>
/// Where the Backend finds gluetun, read once at startup.
/// </summary>
/// <param name="GluetunUrl">The control server's base URL; <see langword="null"/> turns the VPN features off.</param>
/// <param name="DataDirectory">The host directory mounted as gluetun's <c>/gluetun</c>.</param>
/// <param name="ProxyUrl">gluetun's HTTP proxy, which <see cref="VpnProxyProbe"/> checks the tunnel through.</param>
public sealed record VpnConfiguration(Uri? GluetunUrl, string? ApiKey, string? DataDirectory, Uri? ProxyUrl)
{
    public bool IsEnabled => GluetunUrl is not null;

    /// <exception cref="InvalidOperationException"><c>LOOKOUT_GLUETUN_URL</c> is not an absolute URL.</exception>
    public static VpnConfiguration FromConfiguration(IConfiguration configuration)
    {
        static string? Value(IConfiguration configuration, string name) =>
            string.IsNullOrWhiteSpace(configuration[name]) ? null : configuration[name];

        Uri? gluetunUrl = null;
        if (Value(configuration, LookoutEnvironmentVariables.GluetunUrl) is { } url)
        {
            // A trailing slash makes relative request URIs append to any path in the base URL
            if (!Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out gluetunUrl))
            {
                throw new InvalidOperationException(
                    $"Environment variable {LookoutEnvironmentVariables.GluetunUrl} must be an absolute URL");
            }
        }

        Uri? proxyUrl = null;
        if (Value(configuration, LookoutEnvironmentVariables.VpnProxyUrl) is { } proxy
            && !Uri.TryCreate(proxy, UriKind.Absolute, out proxyUrl))
        {
            throw new InvalidOperationException(
                $"Environment variable {LookoutEnvironmentVariables.VpnProxyUrl} must be an absolute URL");
        }

        var dataDirectory = Value(configuration, LookoutEnvironmentVariables.GluetunDir) is { } dir
            ? Path.GetFullPath(dir)
            : null;
        return new VpnConfiguration(
            gluetunUrl, Value(configuration, LookoutEnvironmentVariables.GluetunApiKey), dataDirectory, proxyUrl);
    }
}
