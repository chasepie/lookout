using System.Net;
using System.Text.Json.Serialization;
using ChasePie.Lookout.Backend.Runner;

namespace ChasePie.Lookout.Backend.Vpn;

/// <param name="ApiKey">The key gluetun's control server requires, in <c>X-API-Key</c>, on the routes Lookout uses.</param>
/// <param name="Proxy">gluetun's HTTP proxy, as Browserless's Chromium reaches it.</param>
public sealed record GluetunSettings(string ApiKey, ProxySettings Proxy)
{
    /// <summary>
    /// How long reading the status may take in all. Every VPN Run reads it while holding a Run slot, so an
    /// unresponsive gluetun mustn't hold one up long.
    /// </summary>
    public TimeSpan StatusTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The VPN, through gluetun's control server, whose client's base address is that server (ADR-0004).
/// </summary>
public sealed class GluetunVpnRotator(HttpClient client, GluetunSettings settings, TimeProvider timeProvider) : IVpnRotator
{
    public async Task<VpnStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(settings.StatusTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // gluetun says running as soon as OpenVPN starts, before there's a tunnel, and has a public IP only once
            // there is one, so it takes both to be connected.
            var vpn = await GetAsync<VpnState>("v1/vpn/status", linked.Token);
            if (vpn?.Status != "running")
            {
                return VpnStatus.Disconnected();
            }
            var exit = await GetAsync<PublicIp>("v1/publicip/ip", linked.Token);
            if (string.IsNullOrEmpty(exit?.Ip))
            {
                return VpnStatus.Disconnected();
            }
            return VpnStatus.ConnectedThrough(settings.Proxy, exit.Ip, string.IsNullOrEmpty(exit.Region) ? null : exit.Region);
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return VpnStatus.Disconnected(
                $"gluetun refused Lookout's API key (HTTP {(int)error.StatusCode}). Check that Vpn:ApiKey matches gluetun's.");
        }
        // Anything but the caller cancelling means gluetun can't be read, which leaves VPN Runs refused.
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return VpnStatus.Disconnected(error switch
            {
                _ when timeout.IsCancellationRequested =>
                    $"gluetun's control server did not respond within {settings.StatusTimeout.TotalSeconds:0.#} seconds.",
                HttpRequestException { StatusCode: { } status } => $"gluetun's control server returned HTTP {(int)status}.",
                _ => $"Could not reach gluetun's control server: {error.Message}"
            });
        }
    }

    public async Task RotateAsync(CancellationToken cancellationToken)
    {
        // gluetun picks a random server among its SERVER_REGIONS each time it starts the VPN.
        foreach (var status in new[] { "stopped", "running" })
        {
            using var request = Request(HttpMethod.Put, "v1/vpn/status");
            request.Content = JsonContent.Create(new { status });
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, path);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-API-Key", settings.ApiKey);
        return request;
    }

    private sealed record VpnState(string? Status);

    private sealed record PublicIp([property: JsonPropertyName("public_ip")] string? Ip, string? Region);
}
