namespace ChasePie.Lookout.Backend.Vpn;

/// <summary>Stands in for the VPN when Lookout has none configured, so VPN Runs are refused.</summary>
public sealed class DisabledVpnRotator : IVpnRotator
{
    public Task<VpnStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(VpnStatus.Disabled);

    public Task RotateAsync(CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("Lookout has no VPN configured to rotate."));

    public Task<bool> RefreshServerListAsync(CancellationToken cancellationToken) =>
        Task.FromException<bool>(new InvalidOperationException("Lookout has no VPN configured to refresh."));
}
