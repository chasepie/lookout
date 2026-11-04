using System.Text.Json;

namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// The regions a VPN provider offers, read from the servers list gluetun keeps in its data directory
/// (<c>LOOKOUT_GLUETUN_DIR</c>). gluetun validates region switches against the same list, so any region
/// returned here is one it accepts.
/// </summary>
/// <remarks>
/// gluetun writes one file per provider, <c>servers/&lt;provider name&gt;.json</c>, when it starts and whenever
/// its updater refreshes the list. A file is re-read only after it changes.
/// Providers that group their servers by country or city rather than region have no regions here.
/// </remarks>
public partial class VpnRegionCatalog(VpnConfiguration configuration, ILogger<VpnRegionCatalog> logger)
{
    private readonly Lock _lock = new();
    private CachedRegions? _cached;

    private sealed record CachedRegions(string FilePath, string VpnType, DateTime LastWriteTime, IReadOnlyList<string> Regions);

    /// <summary>
    /// The provider's regions that have servers for the VPN type, sorted, or an empty list if gluetun
    /// hasn't written the file yet.
    /// </summary>
    /// <param name="providerName">gluetun's provider name, such as <c>private internet access</c>.</param>
    /// <param name="vpnType"><c>openvpn</c> or <c>wireguard</c>.</param>
    public IReadOnlyList<string> GetRegions(string providerName, string vpnType)
    {
        if (configuration.DataDirectory is not { } dataDirectory)
        {
            return [];
        }

        // The name comes from gluetun, but it's still used in a path, so refuse anything that isn't a plain name
        var fileName = providerName + ".json";
        if (Path.GetFileName(fileName) != fileName)
        {
            return [];
        }

        var filePath = Path.Join(dataDirectory, "servers", fileName);
        var lastWriteTime = File.GetLastWriteTimeUtc(filePath);
        lock (_lock)
        {
            if (_cached is { } cached && cached.FilePath == filePath && cached.VpnType == vpnType
                && cached.LastWriteTime == lastWriteTime)
            {
                return cached.Regions;
            }
        }

        IReadOnlyList<string> regions;
        try
        {
            regions = ReadRegions(filePath, vpnType);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Missing until gluetun first starts, and briefly incomplete while gluetun rewrites it
            LogReadFailed(ex, filePath);
            return [];
        }

        lock (_lock)
        {
            _cached = new CachedRegions(filePath, vpnType, lastWriteTime, regions);
        }
        return regions;
    }

    private static List<string> ReadRegions(string filePath, string vpnType)
    {
        using var stream = File.OpenRead(filePath);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var regions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers.EnumerateArray())
        {
            if (server.TryGetProperty("vpn", out var vpn) && vpn.GetString() == vpnType
                && server.TryGetProperty("region", out var region) && region.GetString() is { Length: > 0 } name)
            {
                regions.Add(name);
            }
        }
        return [.. regions];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read the VPN servers list at {FilePath}")]
    private partial void LogReadFailed(Exception exception, string filePath);
}
