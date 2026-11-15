namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// A run's use of the VPN, from <see cref="Vpn.TryStartRunAsync"/>: no region switch starts until it's disposed, which
/// the run does when it ends.
/// </summary>
public sealed class VpnRun : IDisposable
{
    private readonly Vpn _vpn;
    private readonly IDisposable _share;
    private int _disposed;
    private int _failoverQueued;

    internal VpnRun(Vpn vpn, IDisposable share, string? region)
    {
        _vpn = vpn;
        _share = share;
        Region = region;
    }

    /// <summary>
    /// The region the run is in, or <see langword="null"/> if the VPN isn't set up or the Backend hasn't made its first
    /// switch yet.
    /// </summary>
    public string? Region { get; }

    /// <summary>
    /// Queues a failover from <see cref="Region"/>, for a run whose known failure asked for one: from now on no VPN run
    /// starts until it's done. Take it before the run is saved as done and before disposing this, so no VPN run starts
    /// in the failed region meanwhile, neither the watch's next run nor another watch's. Call it once.
    /// </summary>
    /// <returns>
    /// The failover, which outlives this lease, or <see langword="null"/> if the VPN isn't set up.
    /// </returns>
    public PendingFailover? QueueFailover()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _failoverQueued, 1) != 0)
        {
            throw new InvalidOperationException("The run has already queued a failover.");
        }

        return _vpn.QueueFailover(this);
    }

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
/// A failover queued by <see cref="VpnRun.QueueFailover"/>, which keeps new VPN runs out until it's run or disposed.
/// </summary>
public sealed class PendingFailover : IDisposable
{
    private readonly Vpn _vpn;
    private readonly string? _failedRegion;
    private VpnGate.SwitchTicket? _ticket;

    internal PendingFailover(Vpn vpn, VpnGate.SwitchTicket ticket, string? failedRegion)
    {
        _vpn = vpn;
        _ticket = ticket;
        _failedRegion = failedRegion;
    }

    /// <summary>
    /// Fails the VPN over from the region the run was in, to a random region rotation may switch to that isn't one of
    /// <paramref name="failedRegions"/>, whether or not rotation is on. Waits for any other switch, then for the VPN
    /// runs in progress, then for the new tunnel. Call it once.
    /// </summary>
    /// <param name="failedRegions">The regions the watch already failed in during its known-failure streak.</param>
    /// <returns>
    /// Whether the VPN has left the region the run was in: it switched, or another switch had already left it. Not if
    /// no region was left to switch to or the switch failed, as <see cref="Core.VpnSettings.LastError"/> then says.
    /// </returns>
    /// <exception cref="InvalidOperationException">The failover has already run, or was abandoned.</exception>
    public Task<bool> FailOverAsync(IReadOnlyCollection<string> failedRegions, CancellationToken cancellationToken)
    {
        var ticket = Interlocked.Exchange(ref _ticket, null)
            ?? throw new InvalidOperationException("The failover has already run, or was abandoned.");
        return _vpn.FailOverAsync(_failedRegion, failedRegions, ticket, cancellationToken);
    }

    /// <summary>
    /// Abandons the failover if it hasn't run, letting VPN runs start again; does nothing once it has.
    /// </summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _ticket, null)?.Dispose();
    }
}
