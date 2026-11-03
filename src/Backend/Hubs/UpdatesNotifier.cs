using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.SignalR;

namespace ChasePie.Lookout.Backend.Hubs;

/// <summary>
/// Sends <see cref="UpdatesHub"/> messages. Call it after a change is saved, so clients never hear
/// about one that was rolled back. Failures are logged, never thrown: a missed message only means
/// a browser shows stale data until its next reload.
/// </summary>
public partial class UpdatesNotifier(
    IHubContext<UpdatesHub, IUpdatesClient> hubContext,
    ILogger<UpdatesNotifier> logger)
{
    public async Task RunUpdatedAsync(WatchRun watchRun)
    {
        try
        {
            await hubContext.Clients.All.RunUpdated(WatchRunUpdate.From(watchRun));
        }
        catch (Exception ex)
        {
            LogSendFailed(ex, nameof(IUpdatesClient.RunUpdated));
        }
    }

    public async Task RunsDeletedAsync(int watchId, IReadOnlyList<int> runIds)
    {
        if (runIds.Count == 0)
        {
            return;
        }

        try
        {
            await hubContext.Clients.All.RunsDeleted(watchId, runIds);
        }
        catch (Exception ex)
        {
            LogSendFailed(ex, nameof(IUpdatesClient.RunsDeleted));
        }
    }

    public async Task WatchChangedAsync(int watchId)
    {
        try
        {
            await hubContext.Clients.All.WatchChanged(watchId);
        }
        catch (Exception ex)
        {
            LogSendFailed(ex, nameof(IUpdatesClient.WatchChanged));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send {Message} to clients")]
    private partial void LogSendFailed(Exception exception, string message);
}
