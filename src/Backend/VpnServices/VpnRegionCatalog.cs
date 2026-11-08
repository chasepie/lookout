using System.Text.Json;

namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// The regions a VPN provider offers, read from the servers list gluetun keeps in its data directory
/// (<c>LOOKOUT_BACKEND_GLUETUN_DIR</c>). gluetun validates region switches against the same list, so any region
/// returned here is one it accepts.
/// </summary>
/// <remarks>
/// gluetun keeps every provider's servers in one file, <c>servers.json</c>, keyed by provider name. It writes the
/// file when it starts and whenever its updater refreshes the list. The file is re-read only after it changes.
/// Providers that group their servers by country or city rather than region have no regions here.
/// </remarks>
public partial class VpnRegionCatalog(VpnConfiguration configuration, ILogger<VpnRegionCatalog> logger)
{
    // Replaced whole, never changed, so readers need no lock
    private volatile CachedRegions? _cached;

    private sealed record CachedRegions(string ProviderName, string VpnType, DateTime LastWriteTime, IReadOnlyList<string> Regions);

    /// <summary>
    /// The regions of the provider and VPN type gluetun is using, sorted, or an empty list if gluetun hasn't
    /// written its servers list yet.
    /// </summary>
    /// <param name="gluetun">
    /// The caller's client. It's a typed HTTP client, so it's transient, and this singleton would otherwise keep
    /// one instance forever, which stops <c>IHttpClientFactory</c> from rotating its handler.
    /// </param>
    /// <exception cref="GluetunException">gluetun couldn't say which provider it's using.</exception>
    public async Task<VpnRegions> GetRegionsAsync(GluetunHttpClient gluetun, CancellationToken cancellationToken)
    {
        var settings = await gluetun.GetVpnSettingsAsync(cancellationToken);
        return new VpnRegions(settings.Provider.Name, GetRegions(settings.Provider.Name, settings.Type));
    }

    /// <param name="providerName">gluetun's provider name, such as <c>private internet access</c>.</param>
    /// <param name="vpnType"><c>openvpn</c> or <c>wireguard</c>.</param>
    private IReadOnlyList<string> GetRegions(string providerName, string vpnType)
    {
        var dataDirectory = configuration.DataDirectory
            ?? throw new InvalidOperationException("The VPN isn't configured, so there's no gluetun data directory.");

        var filePath = Path.Join(dataDirectory, "servers.json");
        var lastWriteTime = File.GetLastWriteTimeUtc(filePath);
        if (_cached is { } cached
            && cached.ProviderName == providerName
            && cached.VpnType == vpnType
            && cached.LastWriteTime == lastWriteTime)
        {
            return cached.Regions;
        }

        IReadOnlyList<string> regions;
        try
        {
            regions = ReadRegions(filePath, providerName, vpnType);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Missing until gluetun first starts, and briefly incomplete while gluetun rewrites it
            LogReadFailed(ex, filePath);
            return [];
        }

        _cached = new CachedRegions(providerName, vpnType, lastWriteTime, regions);
        return regions;
    }

    private static List<string> ReadRegions(string filePath, string providerName, string vpnType)
    {
        using var stream = File.OpenRead(filePath);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(providerName, out var provider)
            || provider.ValueKind != JsonValueKind.Object
            || !provider.TryGetProperty("servers", out var servers)
            || servers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var regions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers.EnumerateArray())
        {
            if (server.TryGetProperty("vpn", out var vpn)
                && vpn.GetString() == vpnType
                && server.TryGetProperty("region", out var region)
                && region.GetString() is { Length: > 0 } name)
            {
                regions.Add(name);
            }
        }
        return [.. regions];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read the VPN servers list at {FilePath}")]
    private partial void LogReadFailed(Exception exception, string filePath);
}

/// <param name="Provider">gluetun's name for the VPN provider, such as <c>private internet access</c>.</param>
public sealed record VpnRegions(string Provider, IReadOnlyList<string> Regions);
