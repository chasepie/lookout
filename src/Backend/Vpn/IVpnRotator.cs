using System.Text.Json.Serialization;
using ChasePie.Lookout.Backend.Runner;

namespace ChasePie.Lookout.Backend.Vpn;

/// <summary>
/// The VPN that VPN Runs go through, behind an interface so gluetun can be replaced (ADR-0004).
/// </summary>
public interface IVpnRotator
{
    /// <summary>
    /// Whether the VPN is connected, through which region and exit IP, and, only while it is, the proxy a VPN Run's
    /// browser goes through.
    /// </summary>
    Task<VpnStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Makes one attempt at switching the VPN to a new exit IP, without waiting for it to connect.</summary>
    Task RotateAsync(CancellationToken cancellationToken);
}

/// <summary>The VPN's state. Only a connected VPN has a proxy, so a VPN Run can't go through one that isn't.</summary>
public sealed record VpnStatus
{
    /// <summary>Whether Lookout is configured with a VPN at all.</summary>
    public bool Enabled { get; private init; }
    /// <summary>Whether VPN Runs can go through it now.</summary>
    public bool Connected => Proxy is not null;
    public string? Region { get; private init; }
    public string? ExitIp { get; private init; }
    /// <summary>Why the VPN's status couldn't be read, if it couldn't.</summary>
    public string? Error { get; private init; }
    /// <summary>The proxy a VPN Run's browser goes through. Left out of the API, as it may carry a password.</summary>
    [JsonIgnore]
    public ProxySettings? Proxy { get; private init; }

    public static readonly VpnStatus Disabled = new();

    public static VpnStatus Disconnected(string? error = null) => new() { Enabled = true, Error = error };

    public static VpnStatus ConnectedThrough(ProxySettings proxy, string exitIp, string? region) =>
        new() { Enabled = true, Proxy = proxy, ExitIp = exitIp, Region = region };
}

/// <summary>Stands in for the VPN when Lookout has none configured, so VPN Runs are refused.</summary>
public sealed class DisabledVpnRotator : IVpnRotator
{
    public Task<VpnStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(VpnStatus.Disabled);

    public Task RotateAsync(CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("Lookout has no VPN configured to rotate."));
}
