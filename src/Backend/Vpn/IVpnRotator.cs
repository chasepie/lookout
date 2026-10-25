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

    /// <summary>
    /// Refreshes the list of servers the VPN picks from, through the VPN, and waits for it to finish. Returns whether
    /// it completed.
    /// </summary>
    Task<bool> RefreshServerListAsync(CancellationToken cancellationToken);
}
