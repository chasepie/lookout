namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// Keys of the Backend's keyed VPN services.
/// </summary>
public static class VpnServiceKeys
{
    /// <summary>
    /// A <see cref="System.Threading.RateLimiting.ConcurrencyLimiter"/> with one permit that keeps VPN runs and
    /// region switches apart: a switch restarts the tunnel, which would cut off a run using it.
    /// <see cref="Services.WatchRunWorker"/> holds it for each run of a watch with <c>UseVpn</c>, and
    /// <see cref="VpnRotationService"/> holds it from asking gluetun to switch until the new tunnel is up.
    /// </summary>
    /// <remarks>
    /// Waiters are served oldest first, so a switch that comes due during a run goes before VPN runs queued
    /// after it. The queue is unbounded, so acquiring always succeeds unless it's cancelled.
    /// </remarks>
    public const string Gate = "vpn-gate";
}
