using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;

namespace ChasePie.Lookout.Backend.IntegrationTests;

public sealed class FakeVpnRotator : IVpnRotator
{
    private int _statusReads;
    private int _rotations;
    private int _serverListRefreshes;

    public VpnStatus Status { get; set; } =
        VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "203.0.113.7", "New York");
    public int StatusReads => Volatile.Read(ref _statusReads);
    public int Rotations => Volatile.Read(ref _rotations);
    public int ServerListRefreshes => Volatile.Read(ref _serverListRefreshes);
    /// <summary>
    /// Called on each attempt at a Rotation, after it's counted. Without it, a connected VPN reconnects at once with
    /// a new exit IP.
    /// </summary>
    public Action? OnRotate { get; set; }

    public Task<VpnStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _statusReads);
        return Task.FromResult(Status);
    }

    public Task RotateAsync(CancellationToken cancellationToken)
    {
        var rotations = Interlocked.Increment(ref _rotations);
        if (OnRotate is not null)
        {
            OnRotate();
        }
        else if (Status is { Connected: true, Proxy: { } proxy })
        {
            Status = VpnStatus.ConnectedThrough(proxy, $"203.0.113.{7 + rotations}", Status.Region);
        }
        return Task.CompletedTask;
    }

    /// <summary>Called on each refresh of the server list, after it's counted. Without it, each refresh completes at once.</summary>
    public Func<Task<bool>>? OnRefreshServerList { get; set; }

    public Task<bool> RefreshServerListAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _serverListRefreshes);
        return OnRefreshServerList?.Invoke() ?? Task.FromResult(true);
    }
}
