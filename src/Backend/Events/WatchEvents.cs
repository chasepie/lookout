using System.Collections.Concurrent;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.SignalR;

namespace ChasePie.Lookout.Backend.Events;

public sealed record RunStartedEvent(Guid WatchId, DateTimeOffset StartedAt);

/// <summary>
/// A Run has finished. A Run that ended without being recorded, such as one superseded by another, has no ID or
/// outcome. The content comes only with a Change.
/// </summary>
public sealed record RunFinishedEvent(Guid WatchId, long? RunId, RunOutcome? Outcome, bool IsChange, string? Content);

public sealed record WatchStatusChangedEvent(Guid WatchId, WatchStatus Status);

/// <summary>
/// Pushes what happens to Watches to the app over <see cref="WatchEventsHub"/>, and knows which Watches are
/// Running: from when a Run starts until it finishes, recorded or not.
/// </summary>
public sealed class WatchEvents(IHubContext<WatchEventsHub, IWatchEventsClient> hub)
{
    private readonly ConcurrentDictionary<Guid, byte> _running = new();

    public bool IsRunning(Guid watchId) => _running.ContainsKey(watchId);

    public Task RunStartedAsync(Guid watchId, DateTimeOffset startedAt)
    {
        _running[watchId] = 0;
        return hub.Clients.All.RunStarted(new RunStartedEvent(watchId, startedAt));
    }

    /// <summary>Announces the end of a Run, with the Run if it was recorded.</summary>
    public Task RunFinishedAsync(Guid watchId, Run? run)
    {
        // Before announcing it, so the app reads the Watch as no longer Running when it reloads on this event.
        _running.TryRemove(watchId, out _);
        return hub.Clients.All.RunFinished(new RunFinishedEvent(watchId, run?.Id, run?.Outcome, run?.IsChange == true,
            run?.IsChange == true ? run.Content : null));
    }

    public Task StatusChangedAsync(Guid watchId, WatchStatus status) =>
        hub.Clients.All.WatchStatusChanged(new WatchStatusChangedEvent(watchId, status));
}
