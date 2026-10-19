using System.Collections.Concurrent;

namespace ChasePie.Lookout.Backend;

/// <summary>Prevents two on-demand Runs from evaluating the same Watch's stale Baseline.</summary>
public sealed class RunningWatches
{
    private readonly ConcurrentDictionary<Guid, byte> _running = new();

    public bool TryStart(Guid id) => _running.TryAdd(id, 0);

    public void Finish(Guid id) => _running.TryRemove(id, out _);
}
