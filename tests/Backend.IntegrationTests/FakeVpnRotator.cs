using ChasePie.Lookout.Backend.Runner;
using ChasePie.Lookout.Backend.Vpn;

namespace ChasePie.Lookout.Backend.IntegrationTests;

public sealed class FakeVpnRotator : IVpnRotator
{
    private int _statusReads;

    public VpnStatus Status { get; set; } =
        VpnStatus.ConnectedThrough(new ProxySettings("http://gluetun:8888"), "203.0.113.7", "New York");
    public int StatusReads => Volatile.Read(ref _statusReads);
    public int Rotations { get; private set; }

    public Task<VpnStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _statusReads);
        return Task.FromResult(Status);
    }

    public Task RotateAsync(CancellationToken cancellationToken)
    {
        Rotations++;
        return Task.CompletedTask;
    }
}
