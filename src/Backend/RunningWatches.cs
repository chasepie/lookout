using System.Collections.Concurrent;

namespace ChasePie.Lookout.Backend;

/// <summary>
/// Stops this process from starting a second Run of a Watch that is already running, whether scheduled or on demand.
/// A Watch counts from when its Run is admitted, including while that Run waits for one of the <see cref="RunSlots"/>.
/// <see cref="Model.Watch.Revision"/> still rejects a stale Run that starts elsewhere.
/// </summary>
public sealed class RunningWatches
{
    /// <summary>Each admitted Watch, and whether its Run has a slot yet.</summary>
    private readonly ConcurrentDictionary<Guid, bool> _admitted = new();

    public bool TryStart(Guid id) => _admitted.TryAdd(id, false);

    /// <summary>Records that the admitted Watch's Run has a slot and is executing.</summary>
    public void MarkExecuting(Guid id) => _admitted[id] = true;

    /// <summary>Whether the Watch's admitted Run is still waiting for a slot.</summary>
    public bool IsWaitingForSlot(Guid id) => _admitted.TryGetValue(id, out var executing) && !executing;

    public void Finish(Guid id) => _admitted.TryRemove(id, out _);
}
