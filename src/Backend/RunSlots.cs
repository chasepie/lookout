namespace ChasePie.Lookout.Backend;

/// <summary>
/// Limits how many Runs execute at once, whether scheduled or on demand, so the browser and target sites
/// aren't overloaded. Browserless's <c>CONCURRENT</c> setting should match the limit.
/// </summary>
public sealed class RunSlots(int limit) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(limit, limit);

    public Task WaitAsync(CancellationToken cancellationToken) => _slots.WaitAsync(cancellationToken);

    public void Release() => _slots.Release();

    public void Dispose() => _slots.Dispose();
}
