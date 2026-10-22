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
/// <param name="proxiedExitIp">Finds the exit IP through the proxy when gluetun's own lookup of it fails.</param>
public sealed class GluetunVpnRotator(
    HttpClient client,
    GluetunSettings settings,
    ProxiedExitIp proxiedExitIp,
    TimeProvider timeProvider) : IVpnRotator
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
            switch (vpn?.Status)
            {
                case "running":
                    break;
                case "crashed":
                    proxiedExitIp.Forget();
                    return VpnStatus.Disconnected("gluetun's VPN crashed. gluetun's logs say why.");
                case var status:
                    proxiedExitIp.Forget();
                    return VpnStatus.Disconnected($"gluetun's VPN is {status ?? "in an unknown state"}.");
            }
            // gluetun clears its public IP whenever the VPN stops or restarts, and has none until the tunnel is up, or
            // if its lookup of it fails, when one found through the proxy counts.
            var exit = await GetAsync<PublicIp>("v1/publicip/ip", linked.Token);
            if (string.IsNullOrEmpty(exit?.Ip))
            {
                // Not held to the deadline for reading gluetun: Browserless takes longer, and other reads share it.
                return await proxiedExitIp.CheckAsync(settings.Proxy, cancellationToken) is { } proxiedIp
                    ? VpnStatus.ConnectedThrough(settings.Proxy, proxiedIp, region: null)
                    : VpnStatus.Disconnected("gluetun is connecting the VPN.");
            }
            proxiedExitIp.Forget();
            return VpnStatus.ConnectedThrough(
                proxy: settings.Proxy,
                exitIp: exit.Ip,
                region: string.IsNullOrEmpty(exit.Region) ? null : exit.Region);
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return VpnStatus.MisconfiguredBecause(
                $"gluetun refused Lookout's API key (HTTP {(int)error.StatusCode}). Check that Vpn:ApiKey matches gluetun's.");
        }
        // Anything but the caller cancelling means gluetun can't be read, which leaves VPN Runs refused.
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return error switch
            {
                _ when timeout.IsCancellationRequested => VpnStatus.Unreachable(
                    $"gluetun's control server did not respond within {settings.StatusTimeout.TotalSeconds:0.#} seconds."),
                // gluetun answered, though not as it should
                HttpRequestException { StatusCode: { } status } =>
                    VpnStatus.Disconnected($"gluetun's control server returned HTTP {(int)status}."),
                _ => VpnStatus.Unreachable($"Could not reach gluetun's control server: {error.Message}")
            };
        }
    }

    /// <summary>How many times a Rotation asks gluetun to start the VPN again before giving up.</summary>
    private const int StartAttempts = 3;

    public async Task RotateAsync(CancellationToken cancellationToken)
    {
        // The old exit is going, and the new tunnel needs time for gluetun to find its exit before a check is worth it.
        proxiedExitIp.Forget();
        await PutVpnStatusAsync("stopped", cancellationToken);
        // gluetun picks a random server among its SERVER_REGIONS each time it starts the VPN. Once it's stopped, nothing
        // else starts it again, so this goes ahead even if the caller gives up, and tries more than once.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await PutVpnStatusAsync("running", CancellationToken.None);
                return;
            }
            catch (Exception error) when (attempt < StartAttempts && error is HttpRequestException or TaskCanceledException)
            {
                // The next attempt follows at once; gluetun answers quickly when it answers at all.
            }
        }
    }

    /// <summary>
    /// Runs gluetun's updater, which fetches the servers through the VPN, unlike its update command, which would go out
    /// from the home IP (ADR-0004).
    /// </summary>
    public Task RefreshServerListAsync(CancellationToken cancellationToken) =>
        PutStatusAsync("v1/updater/status", "running", cancellationToken);

    private Task PutVpnStatusAsync(string status, CancellationToken cancellationToken) =>
        PutStatusAsync("v1/vpn/status", status, cancellationToken);

    private async Task PutStatusAsync(string path, string status, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Put, path);
        request.Content = JsonContent.Create(new { status });
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
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
