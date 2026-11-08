using System.Net.Http.Headers;
using System.Text.Json;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// Typed HTTP client for gluetun's control server (<c>GLUETUN_URL</c>).
/// </summary>
/// <remarks>
/// Switching regions uses <c>PUT /v1/vpn/settings</c>, which the gluetun wiki doesn't document. It merges the
/// body over the current VPN settings and, if they changed, restarts the tunnel: the public IP clears at once
/// and normally comes back when the new tunnel is up, while <c>/v1/vpn/status</c> stays <c>running</c> throughout.
/// gluetun looks the IP up with third-party services once per tunnel, and leaves it empty if they all fail.
/// Settings changed this way last until gluetun restarts.
/// Every failure to get an answer from gluetun, including its error responses, throws <see cref="GluetunException"/>.
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
        using var response = await SendAsync(HttpMethod.Get, "v1/vpn/settings", null, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GluetunVpnSettings>(JsonOptions, cancellationToken)
            ?? throw new GluetunException("gluetun returned empty VPN settings.");
    }

    /// <summary>
    /// Restricts gluetun's servers to <paramref name="region"/>, which restarts the tunnel on a server there.
    /// Returns once gluetun has started reconnecting; <see cref="VpnProbeHttpClient"/> tells when it's done.
    /// </summary>
    /// <exception cref="GluetunException">gluetun rejected the region or couldn't be reached.</exception>
    public async Task SetRegionAsync(string region, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            Provider = new
            {
                ServerSelection = new
                {
                    Regions = new[] { region }
                }
            }
        };
        using var response = await SendAsync(HttpMethod.Put, "v1/vpn/settings", body, cancellationToken);
    }

    /// <summary>
    /// The tunnel's public IP and where it geolocates, or <see langword="null"/> while the tunnel is down,
    /// including while it reconnects after a region switch, or if gluetun's lookup failed.
    /// </summary>
    public async Task<GluetunPublicIp?> GetPublicIpAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "v1/publicip/ip", null, cancellationToken);
        var publicIp = await response.Content.ReadFromJsonAsync<GluetunPublicIp>(JsonOptions, cancellationToken);
        return string.IsNullOrEmpty(publicIp?.PublicIp) ? null : publicIp;
    }

    /// <summary>
    /// Sends a request and returns the successful response, turning every way of not getting one into a
    /// <see cref="GluetunException"/>. gluetun answers errors with a plain-text reason, such as the list of
    /// valid regions for a bad one.
    /// </summary>
    /// <param name="body">Sent as JSON; <see langword="null"/> for no body.</param>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string requestUri, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, requestUri)
        {
            Content = body is null ? null : JsonContent.Create(body, options: JsonOptions),
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new GluetunException($"Couldn't reach gluetun: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's timeout, as opposed to the caller cancelling
            throw new GluetunException($"gluetun didn't answer within {RequestTimeout.TotalSeconds:0} seconds.", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var error = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            throw new GluetunException($"gluetun returned {(int)response.StatusCode}: {error}");
        }
    }

    /// <summary>
    /// Configures the client from <c>GLUETUN_URL</c> and <c>GLUETUN_API_KEY</c>.
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
/// gluetun couldn't be reached, didn't answer in time, or answered with an error.
/// </summary>
public sealed class GluetunException(string message, Exception? innerException = null)
    : Exception(message, innerException);

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
/// Where the Backend finds gluetun, read once at startup. Either every setting is set, or none is.
/// </summary>
/// <param name="GluetunUrl">The control server's base URL; <see langword="null"/> turns the VPN features off.</param>
/// <param name="DataDirectory">The host directory mounted as gluetun's <c>/gluetun</c>.</param>
/// <param name="ProxyUrl">gluetun's HTTP proxy, which <see cref="VpnProbeHttpClient"/> checks the tunnel through.</param>
public sealed record VpnConfiguration(Uri? GluetunUrl, string? ApiKey, string? DataDirectory, Uri? ProxyUrl)
{
    private static readonly VpnConfiguration Disabled = new(null, null, null, null);

    public bool IsEnabled => GluetunUrl is not null;

    /// <exception cref="InvalidOperationException">
    /// <c>GLUETUN_URL</c> is set but isn't an absolute URL, or the settings it needs are missing.
    /// </exception>
    public static VpnConfiguration FromConfiguration(IConfiguration configuration)
    {
        if (EnvironmentVariables.GetBaseUrl(configuration, EnvironmentVariables.GluetunUrl) is not { } gluetunUrl)
        {
            return Disabled;
        }

        var proxyUrl = EnvironmentVariables.GetVpnProxyUrl(configuration)
            ?? throw new InvalidOperationException(
                $"Environment variable {EnvironmentVariables.GluetunProxyUrl} must be set when {EnvironmentVariables.GluetunUrl} is");
        // gluetun's control server refuses every request without it, so the VPN would only look configured
        var apiKey = configuration[EnvironmentVariables.GluetunApiKey];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Environment variable {EnvironmentVariables.GluetunApiKey} must be set when {EnvironmentVariables.GluetunUrl} is");
        }

        return new VpnConfiguration(
            GluetunUrl: gluetunUrl,
            ApiKey: apiKey,
            DataDirectory: EnvironmentVariables.GetRequiredDirectory(configuration, EnvironmentVariables.GluetunDataDir),
            ProxyUrl: proxyUrl);
    }
}
