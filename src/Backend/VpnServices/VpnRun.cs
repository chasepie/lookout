namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// A run's use of the VPN, from <see cref="Vpn.TryStartRunAsync"/>: no region switch starts until it's disposed, which
/// the run does when it ends.
/// </summary>
public sealed class VpnRun : IDisposable
{
    private readonly IDisposable _share;
    private int _disposed;

    internal VpnRun(IDisposable share, string? region)
    {
        _share = share;
        Region = region;
    }

    /// <summary>
    /// The region the run is in, or <see langword="null"/> if the VPN isn't set up.
    /// </summary>
    public string? Region { get; }

    /// <summary>
    /// Ends the run's use of the VPN, so a switch can start once every other VPN run has ended too.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _share.Dispose();
        }
    }
}

/// <summary>
/// What <see cref="Vpn.TryStartRunAsync"/> found when a VPN run tried to start.
/// </summary>
public enum VpnRunStartOutcome
{
    /// <summary>The run may start, holding <see cref="VpnRunStart.Run"/>.</summary>
    Started,

    /// <summary>
    /// No VPN run may start now: a switch is waiting or in progress, or the Backend hasn't made its first switch yet.
    /// </summary>
    HeldUp,

    /// <summary>
    /// The VPN is in a region the run's watch avoids, so the run waits while the VPN switches away from it for the
    /// watch. Other watches' runs may still start.
    /// </summary>
    InAvoidedRegion,

    /// <summary>
    /// The watch avoids every region the VPN may switch to for it, so the run can't start until the Owner clears one.
    /// </summary>
    NoRegionLeft,
}

/// <param name="Run">The run's lease, when <paramref name="Outcome"/> is <see cref="VpnRunStartOutcome.Started"/>.</param>
public sealed record VpnRunStart(VpnRunStartOutcome Outcome, VpnRun? Run = null);
