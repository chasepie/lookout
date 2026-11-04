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

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The public IP of a request sent through the proxy.
    /// </summary>
    /// <exception cref="HttpRequestException">The request didn't get through, as while the tunnel is down.</exception>
    /// <exception cref="TaskCanceledException">The request timed out.</exception>
    public async Task<string?> GetPublicIpAsync(CancellationToken cancellationToken)
    {
        var trace = await httpClient.GetStringAsync(TraceUrl, cancellationToken);
        return trace.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("ip=", StringComparison.Ordinal))?[3..];
    }

    /// <summary>
    /// Sends the client's requests through the VPN proxy. Only registered when
    /// <see cref="VpnConfiguration.ProxyUrl"/> is set.
    /// </summary>
    internal static void Configure(IHttpClientBuilder builder)
    {
        builder
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
