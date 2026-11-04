namespace ChasePie.Lookout.Backend.VpnServices;

/// <summary>
/// Keeps VPN runs and region switches apart: a switch restarts the tunnel, which would cut off a run using it.
/// <see cref="Services.WatchRunWorker"/> holds it for each run of a watch with <c>UseVpn</c>, and
/// <see cref="VpnRotationService"/> holds it from asking gluetun to switch until the new tunnel is up.
/// </summary>
/// <remarks>
/// A switch that comes due during a run waits for it, then goes before the next VPN run: runs execute one at a
/// time, so when a run releases the gate the switch is the only waiter.
/// </remarks>
public sealed class VpnGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Waits until nothing else holds the gate. Dispose the result to release it.
    /// </summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Lease(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
