using System.Collections.Concurrent;

namespace ChasePie.Lookout.Backend;

/// <summary>
/// Stops this process from starting a second Run of a Watch that is already running, whether scheduled or on demand.
/// <see cref="Model.Watch.Revision"/> still rejects a stale Run that starts elsewhere.
/// </summary>
public sealed class RunningWatches
{
    private readonly ConcurrentDictionary<Guid, byte> _running = new();

    public bool TryStart(Guid id) => _running.TryAdd(id, 0);

    public void Finish(Guid id) => _running.TryRemove(id, out _);
}
