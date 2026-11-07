using System.Net;
using System.Net.Sockets;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.HttpOverrides;

namespace ChasePie.Lookout.Backend.Hosting;

/// <summary>
/// Serving Lookout behind a reverse proxy that terminates TLS. The Backend takes the client's address and scheme from
/// <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c>, but only from the proxies in <c>LOOKOUT_TRUSTED_PROXIES</c>:
/// anyone else could set them to dodge the sign-in limit or pass for HTTPS. See docs/security.md.
/// </summary>
public static class ReverseProxy
{
    /// <exception cref="InvalidOperationException">
    /// <c>LOOKOUT_TRUSTED_PROXIES</c> has an entry that isn't an IP or a CIDR network.
    /// </exception>
    public static IServiceCollection AddReverseProxy(this IServiceCollection services, IConfiguration configuration)
    {
        // Parsed here, so a mistyped entry stops startup instead of leaving the proxy untrusted
        var trustedProxies = ParseTrustedProxies(configuration[LookoutEnvironmentVariables.TrustedProxies]);
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // With no known proxies or networks, the middleware trusts every source, so without a configured proxy the
            // headers are ignored altogether
            options.ForwardedHeaders = trustedProxies.Count > 0
                ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
                : ForwardedHeaders.None;
            // One proxy in front of the Backend: only its own entry, the last, is trusted
            options.ForwardLimit = 1;
            // Not even loopback by default: nothing but the configured proxies may set these headers
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var network in trustedProxies)
            {
                options.KnownIPNetworks.Add(network);
            }
        });
        return services;
    }

    /// <summary>
    /// Parses comma-separated IP addresses and CIDR networks. An address is a network of one. IPv4 must be written out in
    /// full, and a network without host bits, so a typo can't parse as some other address.
    /// </summary>
    /// <exception cref="InvalidOperationException">An entry is neither.</exception>
    internal static IReadOnlyList<System.Net.IPNetwork> ParseTrustedProxies(string? value)
    {
        var networks = new List<System.Net.IPNetwork>();
        foreach (var entry in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = entry.IndexOf('/', StringComparison.Ordinal);
            var addressText = slash < 0 ? entry : entry[..slash];
            // IPAddress.Parse also takes shorthand IPv4, such as 172.30.0 for 172.30.0.0
            if (!IPAddress.TryParse(addressText, out var address)
                || (address.AddressFamily == AddressFamily.InterNetwork && address.ToString() != addressText))
            {
                throw InvalidEntry(entry);
            }

            if (slash < 0)
            {
                networks.Add(new System.Net.IPNetwork(address, address.GetAddressBytes().Length * 8));
            }
            else if (System.Net.IPNetwork.TryParse(entry, out var network) && network.BaseAddress.Equals(address))
            {
                networks.Add(network);
            }
            else
            {
                throw InvalidEntry(entry);
            }
        }

        return networks;
    }

    private static InvalidOperationException InvalidEntry(string entry) => new(
        $"{LookoutEnvironmentVariables.TrustedProxies} must list IP addresses or CIDR networks, such as 172.30.0.1 or "
        + $"10.0.0.0/24; '{entry}' is neither.");
}
