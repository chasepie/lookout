using ChasePie.Lookout.Backend.Vpn;
using ChasePie.Lookout.Model;
using Microsoft.AspNetCore.SignalR;

namespace ChasePie.Lookout.Backend.Events;

public sealed record RunStartedEvent(Guid WatchId);

/// <summary>
/// A Run has finished. A Run that ended without being recorded, such as one superseded by another, has no ID or
/// outcome. The content comes only with a Change.
/// </summary>
public sealed record RunFinishedEvent(Guid WatchId, long? RunId, RunOutcome? Outcome, bool IsChange, string? Content);

public sealed record WatchStatusChangedEvent(Guid WatchId, WatchStatus Status);

/// <summary>
/// Pushes what happens to Watches, and to the VPN they may go through, to the app over <see cref="WatchEventsHub"/>.
/// Pushing never fails or holds up a Run for long: a failed or stalled push is logged, and the app catches up when
/// it next reads.
/// </summary>
public sealed partial class WatchEvents(IHubContext<WatchEventsHub, IWatchEventsClient> hub, ILogger<WatchEvents> logger)
{
    /// <summary>How long a push may wait on connections that aren't reading, such as a stalled browser tab.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    public Task RunStartedAsync(Guid watchId) => SendAsync(clients => clients.RunStarted(new RunStartedEvent(watchId)));

    /// <summary>Announces the end of a Run, with the Run if it was recorded.</summary>
    public Task RunFinishedAsync(Guid watchId, Run? run) =>
        SendAsync(clients => clients.RunFinished(
            new RunFinishedEvent(
                WatchId: watchId,
                RunId: run?.Id,
                Outcome: run?.Outcome,
                IsChange: run?.IsChange == true,
                Content: run?.IsChange == true ? run.Content : null)));

    public Task StatusChangedAsync(Guid watchId, WatchStatus status) =>
        SendAsync(clients => clients.WatchStatusChanged(new WatchStatusChangedEvent(watchId, status)));

    public Task VpnStatusChangedAsync(VpnStatus status) => SendAsync(clients => clients.VpnStatusChanged(status));

    private async Task SendAsync(Func<IWatchEventsClient, Task> send)
    {
        try
        {
            await send(hub.Clients.All).WaitAsync(SendTimeout);
        }
        catch (Exception error)
        {
            LogSendFailed(logger, error);
        }
    }

    [LoggerMessage(LogLevel.Warning, "Could not push an event to the app.")]
    private static partial void LogSendFailed(ILogger logger, Exception error);
}
