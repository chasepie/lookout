using System.Net;

namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// Checks that traffic flows through gluetun's HTTP proxy (<c>LOOKOUT_VPN_PROXY_URL</c>), the way watch runs
/// send it. gluetun's firewall blocks the proxy while the tunnel is down, so a request that gets through means
/// the tunnel is up. gluetun's own public IP lookup isn't a reliable signal: it gives up if its IP services fail.
/// </summary>
public class VpnProxyProbe(HttpClient httpClient)
{
    /// <summary>
    /// Cloudflare's trace page: tiny, and it reports the IP the request came from.
    /// </summary>
    private static readonly Uri TraceUrl = new("https://www.cloudflare.com/cdn-cgi/trace");

    /// <summary>
    /// The public IP of a request sent through the proxy, or <see langword="null"/> if the request didn't get
    /// through or timed out, as while the tunnel is down.
    /// </summary>
    public async Task<string?> TryGetPublicIpAsync(CancellationToken cancellationToken)
    {
        string trace;
        try
        {
            trace = await httpClient.GetStringAsync(TraceUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }

        // The request got through, so the tunnel is up even if the page has changed shape
        return trace.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("ip=", StringComparison.Ordinal))?[3..] ?? "unknown";
    }
}

internal static class VpnProxyProbeExtensions
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    extension(IHttpClientBuilder builder)
    {
        /// <summary>
        /// Sends the client's requests through the VPN proxy, <see cref="VpnConfiguration.ProxyUrl"/>.
        /// </summary>
        public IHttpClientBuilder ConfigureVpnProxyProbe() => builder
            .ConfigureHttpClient(client => client.Timeout = RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(services => new SocketsHttpHandler
            {
                Proxy = new WebProxy(services.GetRequiredService<VpnConfiguration>().ProxyUrl),
                UseProxy = true,
                // A connection opened through the old tunnel must not make the new one look up
                PooledConnectionLifetime = TimeSpan.Zero,
            });
    }
}
