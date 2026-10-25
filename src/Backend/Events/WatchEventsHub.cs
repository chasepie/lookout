using ChasePie.Lookout.Backend.Vpn;
using ChasePie.Lookout.Database;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ChasePie.Lookout.Backend.Events;

/// <summary>The events Lookout pushes to the app, named as the app listens for them.</summary>
public interface IWatchEventsClient
{
    Task RunStarted(RunStartedEvent message);
    Task RunFinished(RunFinishedEvent message);
    Task WatchStatusChanged(WatchStatusChangedEvent message);
    Task VpnStatusChanged(VpnStatus message);
}

/// <summary>
/// Pushes <see cref="WatchEvents"/> to the app as they happen. Like the API, it requires a login, and the
/// connection is negotiated with the request token (see <see cref="RequestForgery.ProtectHub"/>).
/// </summary>
public sealed class WatchEventsHub(EventConnections connections, LookoutDbContext database) : Hub<IWatchEventsClient>
{
    public const string Path = "/api/events";

    public override async Task OnConnectedAsync()
    {
        connections.Add(Context);
        // A logout between authenticating this connection and adding it closed the Session's connections without it.
        var sessionId = SessionCookie.SessionId(Context.User);
        if (sessionId is null || !await database.Sessions.AnyAsync(session => session.Id == sessionId, Context.ConnectionAborted))
        {
            Context.Abort();
        }
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context);
        return base.OnDisconnectedAsync(exception);
    }
}
